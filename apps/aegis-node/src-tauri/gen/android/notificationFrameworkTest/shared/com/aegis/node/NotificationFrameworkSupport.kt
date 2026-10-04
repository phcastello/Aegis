package com.aegis.node

import android.Manifest
import android.app.Activity
import android.app.NotificationManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.test.platform.app.InstrumentationRegistry

internal const val NODE_NOTIFICATION_CHANNEL = "aegis_node_notifications"
internal fun notificationActivityClass(): Class<out Activity> = Class.forName(
  "com.aegis.node.MainActivity", true, InstrumentationRegistry.getInstrumentation().targetContext.classLoader
).asSubclass(Activity::class.java)

// Verify the permission in the framework independently of the target's private implementation.
internal fun frameworkNotificationPermission(context: Context): Boolean =
  (Build.VERSION.SDK_INT < 33 || context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) &&
    context.getSystemService(NotificationManager::class.java).areNotificationsEnabled()
