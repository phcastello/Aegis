package com.aegis.node

import android.app.Activity
import android.app.NotificationManager
import android.content.ContentValues
import android.content.Context
import android.content.ContextWrapper
import android.content.SharedPreferences
import android.database.DatabaseErrorHandler
import android.database.sqlite.SQLiteDatabase
import android.util.Log
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/** Real Android SQLite and the already-kept JNI entry points, including R8. */
@RunWith(AndroidJUnit4::class)
class NodeReminderDatabaseConcurrencyFrameworkTest {
    private val instrumentation = InstrumentationRegistry.getInstrumentation()
    private val context = instrumentation.targetContext

    private class IsolatedContext(base: Context, val directory: File, private val prefix: String) : ContextWrapper(base) {
        private val preferenceNames = ConcurrentHashMap.newKeySet<String>()

        // API 26 can open through Context.openOrCreateDatabase; newer helpers
        // use getDatabasePath. Both must point at our newly created fixture.
        override fun getDatabasePath(name: String): File {
            require(name == DATABASE)
            return File(directory, name)
        }
        override fun openOrCreateDatabase(name: String, mode: Int, factory: SQLiteDatabase.CursorFactory?): SQLiteDatabase =
            openOrCreateDatabase(name, mode, factory, null)
        override fun openOrCreateDatabase(name: String, mode: Int, factory: SQLiteDatabase.CursorFactory?, errorHandler: DatabaseErrorHandler?): SQLiteDatabase {
            val flags = SQLiteDatabase.CREATE_IF_NECESSARY or
                (if (mode and Context.MODE_ENABLE_WRITE_AHEAD_LOGGING != 0) SQLiteDatabase.ENABLE_WRITE_AHEAD_LOGGING else 0)
            return SQLiteDatabase.openDatabase(getDatabasePath(name).path, factory, flags, errorHandler)
        }
        override fun getSharedPreferences(name: String?, mode: Int): SharedPreferences {
            val isolated = "$prefix-${name ?: "default"}"
            preferenceNames.add(isolated)
            return super.getSharedPreferences(isolated, mode)
        }
        fun deleteFixturePreferences() { preferenceNames.forEach { super.deleteSharedPreferences(it) } }
    }

    private fun seedLegacy(file: File, actionId: String) {
        SQLiteDatabase.openOrCreateDatabase(file, null).use { db ->
            // Original on-disk v1 schema before additive action diagnostics.
            db.execSQL("CREATE TABLE presentations (id TEXT PRIMARY KEY, generation INTEGER NOT NULL, revision INTEGER NOT NULL, token TEXT, expires INTEGER NOT NULL, commandId TEXT NOT NULL,ruleId TEXT,ruleRevision INTEGER)")
            db.execSQL("CREATE TABLE withdrawals (id TEXT PRIMARY KEY,generation INTEGER NOT NULL,expires INTEGER NOT NULL)")
            db.execSQL("CREATE TABLE actions (actionId TEXT PRIMARY KEY, reminderId TEXT NOT NULL, revision INTEGER NOT NULL, generation INTEGER NOT NULL, action TEXT NOT NULL, token TEXT, created INTEGER NOT NULL)")
            val row = ContentValues().apply {
                put("actionId", actionId); put("reminderId", UUID.randomUUID().toString())
                put("revision", 1); put("generation", 1); put("action", "acknowledge")
                put("token", "disposable-test-token"); put("created", System.currentTimeMillis())
            }
            check(db.insertOrThrow("actions", null, row) >= 0)
            db.version = 1
        }
    }

    private fun exercise(legacy: Boolean) {
        val prefix = "reminder-db-fixture-${UUID.randomUUID()}"
        val directory = File(context.cacheDir, prefix)
        check(directory.mkdir() && directory.canonicalFile.parentFile == context.cacheDir.canonicalFile)
        val isolated = IsolatedContext(context, directory, prefix)
        val file = isolated.getDatabasePath(DATABASE)
        check(file.canonicalFile != context.getDatabasePath(DATABASE).canonicalFile && !file.exists())
        val actionId = UUID.randomUUID().toString()
        if (legacy) seedLegacy(file, actionId)
        val reminderId = UUID.randomUUID().toString()
        val command = JSONObject().put("commandId", UUID.randomUUID().toString())
            .put("capability", "reminder.notify").put("capabilityVersion", 1)
            .put("expiresAt", Instant.now().plusSeconds(120).toString())
            .put("input", JSONObject().put("title", "Aegis framework fixture").put("body", "Concurrent SQLite open")
                .put("reminderId", reminderId).put("revision", 1).put("generation", 1).put("interactionToken", "isolated-framework-token-value"))
            .toString()
        val workers = 8
        val activities = ArrayList<Activity>(workers)
        val attach = ContextWrapper::class.java.getDeclaredMethod("attachBaseContext", Context::class.java).apply { isAccessible = true }
        val target = notificationActivityClass()
        val read = target.getMethod("nodeActionState", String::class.java)
        val write = target.getMethod("showNodeNotification", String::class.java)
        val manager = context.getSystemService(NotificationManager::class.java)
        val pool = Executors.newFixedThreadPool(workers)
        val ready = CountDownLatch(workers)
        val start = CountDownLatch(1)
        try {
            // Same real Activity/JNI context as the existing notification
            // framework fixtures, without launching UI or touching its DB.
            instrumentation.runOnMainSync {
                repeat(workers) {
                    val activity = target.getDeclaredConstructor().newInstance()
                    attach.invoke(activity, isolated)
                    activities.add(activity)
                }
            }
            val results = activities.mapIndexed { index, activity -> pool.submit {
                ready.countDown()
                check(start.await(10, TimeUnit.SECONDS))
                if (index == 0) {
                    val result = JSONObject(write.invoke(activity, command) as String)
                    assertEquals("Actual reminder write succeeds during concurrent first open", "success", result.getString("status"))
                } else {
                    val result = JSONObject(read.invoke(activity, "{}") as String)
                    assertEquals("Actual reminder diagnostic reads preserve queued actions", if (legacy) 1 else 0, result.getInt("pendingActions"))
                }
            } }
            check(ready.await(10, TimeUnit.SECONDS))
            start.countDown()
            for (result in results) result.get(30, TimeUnit.SECONDS)
            awaitFrameworkReminder(manager, reminderId, 1)
            SQLiteDatabase.openDatabase(file.path, null, SQLiteDatabase.OPEN_READONLY).use { db ->
                assertEquals(1, db.version)
                val tables = db.rawQuery("SELECT name FROM sqlite_master WHERE type='table'", null).use { rows ->
                    buildSet { while (rows.moveToNext()) add(rows.getString(0)) }
                }
                assertTrue(tables.containsAll(setOf("presentations", "withdrawals", "actions", "metadata", "withdrawal_receipts", "action_history")))
                val columns = db.rawQuery("PRAGMA table_info(actions)", null).use { rows ->
                    buildSet { while (rows.moveToNext()) add(rows.getString(1)) }
                }
                assertTrue(columns.containsAll(setOf("attempts", "lastAttempt", "lastResult", "nextAttempt")))
                db.rawQuery("SELECT id,generation,revision FROM presentations", null).use { rows ->
                    assertTrue(rows.moveToFirst()); assertEquals(reminderId, rows.getString(0))
                    assertEquals(1L, rows.getLong(1)); assertEquals(1L, rows.getLong(2)); assertFalse(rows.moveToNext())
                }
                if (legacy) {
                    db.rawQuery("SELECT action,token,attempts,lastResult FROM actions WHERE actionId=?", arrayOf(actionId)).use { rows ->
                        assertTrue(rows.moveToFirst()); assertEquals("acknowledge", rows.getString(0))
                        assertTrue(rows.getString(1) == "disposable-test-token"); assertEquals(0, rows.getInt(2))
                        assertEquals("queued", rows.getString(3)); assertFalse(rows.moveToNext())
                    }
                    assertTrue("Additive migration retains its existing backup", File(directory, "$DATABASE.pre-action-diagnostics.bak").isFile)
                }
            }
        } finally {
            start.countDown()
            pool.shutdownNow()
            val stopped = pool.awaitTermination(10, TimeUnit.SECONDS)
            manager.cancel("reminder:$reminderId:1", 1)
            awaitFrameworkReminderWithdrawal(manager, reminderId, 1)
            // Never remove an open database or a path owned by the real app.
            if (stopped) {
                isolated.deleteFixturePreferences()
                check(directory.deleteRecursively())
            }
            check(stopped)
        }
    }

    @Test fun simultaneousReadersAndWriterCreateAndMigrateOnlyOneSchema() {
        assertTrue(frameworkNotificationPermission(context))
        repeat(4) { exercise(legacy = false) }
        exercise(legacy = true)
        Log.i("AegisNodeNativeTest", "phase=reminder_database_concurrent_open errorType=Checkpoint")
    }

    companion object { private const val DATABASE = "aegis-native-reminders-v1.db" }
}
