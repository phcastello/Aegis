package com.aegis.node

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import java.util.Base64

class NodeWakeDatasetTest {
    @Test fun alreadyListeningDetectorNeverExportsPcmFromBeforeConsentOrAnEarlierGeneration() {
        val ring = HandsFreeRing()
        val window = WakeDatasetConsentWindow()
        var total = 0L
        fun read(before: String?, after: String?, marker: Short): ShortArray? {
            val data = ShortArray(3200) { marker }
            ring.append(data, data.size)
            val boundary = HandsFreeActivationBoundary(total, data.size, total + 1600)
            total += data.size
            return window.received(before, after, data.size)?.let { window.context(ring, boundary) }
        }
        repeat(12) { assertNull(read(null, null, 10)) }
        assertNull(read(null, "first", 20)) // Opted in during the blocking microphone read.
        assertArrayEquals(ShortArray(3200) { 30 }, read("first", "first", 30))
        assertArrayEquals(ShortArray(3200) { 30 } + ShortArray(3200) { 40 }, read("first", "first", 40))
        assertNull(read("first", "second", 50)) // Disabled and re-enabled within a single read.
        assertArrayEquals(ShortArray(3200) { 60 }, read("second", "second", 60))
        assertNull(read("second", null, 70))
        assertArrayEquals(ShortArray(3200) { 80 }, read("third", "third", 80))
    }

    @Test fun recoveryResumesOnlyDurableSamplesFromCurrentConsentAndKeepsFinalMetadata() {
        val root = java.nio.file.Files.createTempDirectory("wake-outbox-test").toFile()
        try {
            val current = root.resolve("current").apply { mkdirs() }
            val old = root.resolve("revoked").apply { mkdirs() }
            old.resolve("private.json").writeText("revoked")
            current.resolve("pending.json").writeText("final")
            current.resolve("pending.hold").writeText("older")
            current.resolve("interrupted.hold").writeText("unknown")
            current.resolve("partial.tmp").writeText("partial")
            current.resolve("recused.rejected").writeText("recused")
            current.resolve("recused.hold").writeText("duplicate")
            val scheduled = mutableListOf<Pair<String, String>>()
            recoverWakeDataset(root, "current") { epoch, id -> scheduled.add(epoch to id) }
            assertEquals(listOf("current" to "interrupted", "current" to "pending"), scheduled.sortedBy { it.second })
            assertFalse(old.exists())
            assertEquals("final", current.resolve("pending.json").readText())
            assertEquals("unknown", current.resolve("interrupted.json").readText())
            assertFalse(current.resolve("partial.tmp").exists())
            assertFalse(current.resolve("recused.hold").exists())
            assertEquals(WakeDatasetQueueFiles(2, 1, 19), wakeDatasetQueueFiles(current))
            recoverWakeDataset(root, null) { _, _ -> fail("Revoked samples must never be scheduled") }
            assertFalse(current.exists())
        } finally { root.deleteRecursively() }
    }

    @Test fun suppressedCandidateKeepsContextAndCommandAudioWithoutGivingEitherAHumanLabel() {
        val metadata = JSONObject().put("groupId", "demo").put("transcript", "private")
        val clip = WakeDatasetClip("epoch", 12, shortArrayOf(11, 12), metadata)
        clip.disposition = "election_lost"
        clip.append(shortArrayOf(0, 31, 32), 1, 2)
        val payload = clip.payload()
        assertEquals("unknown", payload.getString("commandDisposition"))
        assertFalse(payload.has("humanLabel")); assertFalse(payload.has("transcript"))
        assertArrayEquals(handsFreeWav(shortArrayOf(31, 32), 2), Base64.getDecoder().decode(payload.getString("postWav")))
        assertArrayEquals(handsFreeWav(shortArrayOf(11, 12), 2), Base64.getDecoder().decode(payload.getString("wav")))
        assertEquals(64, payload.getString("sha256").length)
        clip.append(ShortArray(60000), 0, 60000)
        assertTrue(clip.complete)
        assertEquals(44 + 2 * 48000, Base64.getDecoder().decode(clip.payload().getString("postWav")).size)
        clip.clear()
    }
    @Test fun winnerCanCaptureTheFirstCommandBeyondThreeSeconds() {
        val clip = WakeDatasetClip("epoch", 12, shortArrayOf(12), JSONObject())
        clip.disposition = "activated"
        clip.append(ShortArray(55000), 0, 55000)
        assertEquals(44 + 2 * 55000, Base64.getDecoder().decode(clip.payload().getString("postWav")).size)
    }

    @Test fun electionEpisodeReplacesOnlyTheCapturedWakeObservationsLocalGrouping() {
        val local = "129edd9c-f203-409c-85fd-89a759341abe"
        val episode = "e602008a-7283-43ae-97a0-ab3238c731c9"
        for (origin in listOf("wake_word", "diagnostic")) {
            val meta = JSONObject().put("groupId", local).put("origin", origin)
            val clip = WakeDatasetClip("epoch", 12, shortArrayOf(11, 12), meta)
            clip.append(shortArrayOf(31, 32), 0, 2)
            val captured = clip.payload()
            clip.groupWithEpisode(null)
            assertEquals(local, clip.payload().getString("groupId"))
            clip.groupWithEpisode(episode)
            val resolved = clip.payload()
            assertEquals(if (origin == "wake_word") episode else local, resolved.getString("groupId"))
            assertEquals(1, resolved.getInt("schemaVersion"))
            assertFalse(resolved.has("episodeId"))
            assertEquals(captured.getString("sha256"), resolved.getString("sha256"))
            assertEquals(captured.getString("postSha256"), resolved.getString("postSha256"))
            clip.clear()
        }
    }
}
