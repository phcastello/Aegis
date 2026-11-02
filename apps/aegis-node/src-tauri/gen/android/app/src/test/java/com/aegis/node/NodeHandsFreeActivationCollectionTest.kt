package com.aegis.node

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.file.Files
import java.security.MessageDigest
import java.time.OffsetDateTime
import java.util.zip.ZipInputStream

class NodeHandsFreeActivationCollectionTest {
    @Test fun crashDuringWavHeaderWriteRebuildsCanonicalHeaderAndPreservesRecoverablePcm() {
        for(headerLength in listOf(0,10,44)) {
            val(store,dir)=store()
            try {
                store.begin(JSONObject(),ShortArray(0))!!.let{store.appendPost(it,shortArrayOf(101,-202,303))};store.awaitIdle()
                val wav=dir.walkTopDown().single{it.name==HandsFreeActivationCollection.POST}
                java.io.RandomAccessFile(wav,"rw").use{file->if(headerLength<44)file.setLength(headerLength.toLong()) else {file.seek(0);file.writeInt(0)}}
                val recovered=HandsFreeActivationCollection(dir,asset={byteArrayOf()});recovered.awaitIdle()
                val zip=archive(recovered);val row=JSONObject(zip.getValue("manifest.jsonl").decodeToString().trim())
                val audio=row.getJSONObject("postActivation");val bytes=zip.getValue(audio.getString("path"))
                val expected=if(headerLength<44)ShortArray(0) else shortArrayOf(101,-202,303)
                assertArrayEquals(handsFreeWav(expected,expected.size),bytes)
                assertEquals(hash(bytes),audio.getString("sha256"));assertEquals(expected.size,audio.getInt("samples"))
                assertTrue(row.getJSONArray("storageIssues").toString().contains("wav_header_repaired"))
                assertTrue(row.isNull("endedAt"));assertEquals("interrupted",row.getString("finalState"))
            }finally{dir.deleteRecursively()}
        }
    }
    @Test fun failedSnapshotCleansStagingAndPreservesOriginalEvents() {
        val(store,dir)=store()
        try {
            val id=store.begin(JSONObject(),shortArrayOf(1,2))!!
            store.appendPost(id,shortArrayOf(3,4));store.finish(id,"cancelled","explicit_cancel");store.awaitIdle()
            val original=dir.walkTopDown().single{it.name==HandsFreeActivationCollection.POST}.readBytes()
            assertThrows(java.util.concurrent.ExecutionException::class.java) {
                store.export(ByteArrayOutputStream()){throw java.io.IOException("synthetic_snapshot_interruption")}
            }
            assertTrue(dir.parentFile!!.listFiles()!!.none{it.name.startsWith("activation-export-")})
            val zip=archive(store);val row=JSONObject(zip.getValue("manifest.jsonl").decodeToString().trim())
            assertEquals(id,row.getString("id"));assertArrayEquals(original,zip.getValue(row.getJSONObject("postActivation").getString("path")))
        }finally{dir.deleteRecursively()}
    }
    private fun store(enabled:Boolean=true):Pair<HandsFreeActivationCollection,java.io.File> {
        val dir=Files.createTempDirectory("aegis-activation-test").toFile()
        val store=HandsFreeActivationCollection(dir,initialEnabled=enabled,asset={"asset:$it".toByteArray()},now={OffsetDateTime.parse("2026-11-01T21:30:00-03:00")})
        store.awaitIdle();return store to dir
    }
    private fun archive(store:HandsFreeActivationCollection):Map<String,ByteArray> {
        val out=ByteArrayOutputStream();store.export(out,"{\"schema\":2,\"historical\":{\"old\":123}}")
        val map=linkedMapOf<String,ByteArray>()
        ZipInputStream(out.toByteArray().inputStream()).use{zip->while(true){val entry=zip.nextEntry?:break;map[entry.name]=zip.readBytes()}}
        return map
    }
    private fun hash(bytes:ByteArray)=MessageDigest.getInstance("SHA-256").digest(bytes).joinToString(""){"%02x".format(it)}
    @Test fun cancellationBeforeFirstPostSampleStillExportsTwoValidWavsAndUnknownHumanLabel() {
        val(store,dir)=store()
        try {
            val id=store.begin(JSONObject().put("source","manual").put("instantaneousScore",JSONObject.NULL),ShortArray(0))!!
            store.finish(id,"cancelled","explicit_user_cancel")
            val zip=archive(store);val row=JSONObject(zip.getValue("manifest.jsonl").decodeToString().trim())
            assertEquals(id,row.getString("id"));assertTrue(row.isNull("humanLabel"));assertEquals("2026-11-01T21:30-03:00",row.getString("activatedAt"))
            assertEquals("explicit_user_cancel",row.getString("endReason"));assertEquals("not_started",row.getString("transcriptionStatus"))
            for(key in listOf("triggerContext","postActivation")) {
                val audio=row.getJSONObject(key);val bytes=zip.getValue(audio.getString("path"))
                assertEquals(44,bytes.size);assertEquals("RIFF",bytes.copyOfRange(0,4).decodeToString());assertEquals(0,ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).getInt(40))
                assertEquals(hash(bytes),audio.getString("sha256"));assertEquals(0,audio.getLong("samples"))
            }
            assertEquals("{\"schema\":2,\"historical\":{\"old\":123}}",zip.getValue("metrics.json").decodeToString())
            assertTrue(zip.keys.containsAll(listOf("index.html","events.js","reviewer.js","reviewer.css","README.txt")))
        } finally{dir.deleteRecursively()}
    }
    @Test fun snapshotDuringCaptureIsConsistentAndDoesNotCloseOriginalRecording() {
        val(store,dir)=store()
        try {
            val id=store.begin(JSONObject().put("source","wake_word"),shortArrayOf(1,2,3))!!
            store.appendPost(id,shortArrayOf(4,5))
            val first=archive(store);val row=JSONObject(first.getValue("manifest.jsonl").decodeToString().trim())
            assertTrue(row.getBoolean("snapshotInProgress"));assertTrue(row.isNull("endedAt"));assertEquals(2,row.getJSONObject("postActivation").getInt("samples"))
            store.appendPost(id,shortArrayOf(6));store.finishCapture(id);store.finish(id,"no_speech","no_speech")
            val second=archive(store);val final=JSONObject(second.getValue("manifest.jsonl").decodeToString().trim())
            assertFalse(final.getBoolean("snapshotInProgress"));assertEquals(3,final.getJSONObject("postActivation").getInt("samples"))
            assertEquals(2,row.getJSONObject("postActivation").getInt("samples"));assertEquals(id,final.getString("id"))
            assertEquals(hash(second.getValue(final.getJSONObject("postActivation").getString("path"))),final.getJSONObject("postActivation").getString("sha256"))
        } finally{dir.deleteRecursively()}
    }
    @Test fun abruptRestartRepairsWavHeadersAndMarksObservedRecoveryWithoutInventingCrashTime() {
        val(store,dir)=store()
        try {
            val id=store.begin(JSONObject(),shortArrayOf(8,9))!!;store.appendPost(id,shortArrayOf(10,11,12));store.awaitIdle()
            val file=dir.walkTopDown().single{it.name==HandsFreeActivationCollection.POST}
            java.io.RandomAccessFile(file,"rw").use{it.seek(it.length());it.write(byteArrayOf(13,0,99))}
            val recovered=HandsFreeActivationCollection(dir,asset={byteArrayOf()},now={OffsetDateTime.parse("2026-11-02T09:00:00-03:00")});recovered.awaitIdle()
            val zip=archive(recovered);val row=JSONObject(zip.getValue("manifest.jsonl").decodeToString().trim())
            assertEquals(id,row.getString("id"));assertEquals("interrupted",row.getString("finalState"));assertEquals("process_restart_unknown",row.getString("endReason"));assertTrue(row.isNull("interruptionAt"))
            assertEquals(4,row.getJSONObject("postActivation").getInt("samples"));assertEquals(52,file.length())
        } finally{dir.deleteRecursively()}
    }
    @Test fun disabledCollectionStoresNothingAndDeletionRequiresNoActiveSession() {
        val(store,dir)=store(false)
        try {
            assertNull(store.begin(JSONObject(),shortArrayOf(1)));assertTrue(archive(store).getValue("manifest.jsonl").isEmpty())
            store.configure(true,25);val id=store.begin(JSONObject(),ShortArray(0))!!
            var refused=false;try{store.deleteAll()}catch(_:java.util.concurrent.ExecutionException){refused=true};assertTrue(refused)
            store.finish(id,"cancelled","explicit_user_cancel");store.awaitIdle();store.deleteAll();assertEquals(0,store.state().getLong("bytes"))
        } finally{dir.deleteRecursively()}
    }
    @Test fun optOutStopsExistingAudioAndReenableDoesNotResumeIt() {
        val(store,dir)=store()
        try {
            val id=store.begin(JSONObject(),shortArrayOf(9))!!
            store.appendPost(id,shortArrayOf(1,2));store.configure(false,25)
            store.appendPost(id,shortArrayOf(3,4));store.appendContext(id,shortArrayOf(8))
            store.configure(true,25);store.appendPost(id,shortArrayOf(5,6));store.finish(id,"completed",null)
            val row=JSONObject(archive(store).getValue("manifest.jsonl").decodeToString().trim())
            assertEquals(2,row.getJSONObject("postActivation").getInt("samples"));assertEquals(1,row.getJSONObject("triggerContext").getInt("samples"))
            assertEquals("collection_disabled",row.getString("captureEndReason"))
        } finally{dir.deleteRecursively()}
    }
    @Test fun boundedQueuePressureRetainsAcceptedPcmBeforeCancellationAndReportsDroppedFrames() {
        val(store,dir)=store()
        try {
            val id=store.begin(JSONObject(),ShortArray(0))!!;store.awaitIdle()
            val executor=store.javaClass.getDeclaredField("executor").also{it.isAccessible=true}.get(store) as java.util.concurrent.ThreadPoolExecutor
            val started=java.util.concurrent.CountDownLatch(1);val release=java.util.concurrent.CountDownLatch(1)
            executor.execute{started.countDown();release.await()};assertTrue(started.await(5,java.util.concurrent.TimeUnit.SECONDS))
            try {repeat(300){store.appendPost(id,shortArrayOf(it.toShort()))};store.finish(id,"cancelled","explicit_user_cancel")}
            finally{release.countDown()}
            val zip=archive(store);val row=JSONObject(zip.getValue("manifest.jsonl").decodeToString().trim())
            assertEquals("cancelled",row.getString("finalState"));assertTrue(row.getJSONArray("captureIssues").toString().contains("persistence_queue_full"))
            assertEquals(256,row.getJSONObject("postActivation").getInt("samples"))
            val pcm=ByteBuffer.wrap(zip.getValue(row.getJSONObject("postActivation").getString("path"))).order(ByteOrder.LITTLE_ENDIAN)
            repeat(256){assertEquals(it.toShort(),pcm.getShort(44+it*2))}
        }finally{dir.deleteRecursively()}
    }
    @Test fun quotaNeverDeletesOriginalsAndSkippedCountSurvivesRestart() {
        val dir=Files.createTempDirectory("aegis-activation-quota").toFile()
        try {
            val preserved=java.io.File(dir,"preserved-recording");java.io.RandomAccessFile(preserved,"rw").use{it.setLength(10L*1024*1024)}
            val store=HandsFreeActivationCollection(dir,initialEnabled=true,initialLimitMiB=10);store.awaitIdle()
            assertNull(store.begin(JSONObject(),ShortArray(0)));store.awaitIdle()
            assertEquals("storage_limit",store.state().getString("status"));assertEquals(1,store.state().getLong("skippedEvents"));assertEquals(10L*1024*1024,preserved.length())
            val restarted=HandsFreeActivationCollection(dir,initialEnabled=true,initialLimitMiB=10);restarted.awaitIdle()
            assertEquals(1,restarted.state().getLong("skippedEvents"));assertTrue(preserved.exists())
        }finally{dir.deleteRecursively()}
    }
    @Test fun unwritableStoreReportsFailureWithoutThrowingOnCaptureCaller() {
        val parent=Files.createTempDirectory("aegis-activation-failure").toFile()
        try {
            val invalid=java.io.File(parent,"not-a-directory");invalid.writeText("occupied")
            val store=HandsFreeActivationCollection(invalid,initialEnabled=true);store.awaitIdle()
            val id=store.begin(JSONObject(),ShortArray(0));assertNotNull(id);store.finish(id,"failed","capture_failure");store.awaitIdle()
            assertEquals("storage_failure",store.state().getString("status"));assertEquals(1,store.state().getLong("skippedEvents"));assertEquals("occupied",invalid.readText())
        }finally{parent.deleteRecursively()}
    }
    @Test fun finishCaptureDoesNotRetainLaterProcessingOrTtsPcm() {
        val(store,dir)=store()
        try {
            val id=store.begin(JSONObject(),ShortArray(0))!!
            store.appendPost(id,shortArrayOf(1));store.appendPost(id,shortArrayOf(2));store.finishCapture(id);store.appendPost(id,shortArrayOf(3,4));store.finish(id,"failed","backend_failure","failed")
            val row=JSONObject(archive(store).getValue("manifest.jsonl").decodeToString().trim())
            assertEquals(2,row.getJSONObject("postActivation").getInt("samples"));assertEquals("failed",row.getString("transcriptionStatus"))
        } finally{dir.deleteRecursively()}
    }
}
