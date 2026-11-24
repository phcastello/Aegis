package com.aegis.node

import org.junit.Assert.*
import org.junit.Test

class NodeHandsFreeConversationAudioTest {
    private class Port : HandsFreeNativeAecPort<String> {
        val operations = mutableListOf<String>()
        var availability: String? = null
        var modeAvailable = true
        var routeAvailable = true
        var ready = true
        var effectAvailable = true
        var effectActive = true
        var gain: Float? = 0.5f
        var activeMode = false
        var activeRoute = false
        var failRegistration = false
        var failEffectRelease = false
        var recordingFailure: String? = null

        override fun availabilityIssue() = availability

        override fun enterMode(): Boolean {
            operations.add("request_mode")
            activeMode = modeAvailable
            return activeMode
        }

        override fun modeActive() = activeMode

        override fun selectSpeaker(): Boolean {
            operations.add("request_speaker")
            activeRoute = routeAvailable
            return activeRoute
        }

        override fun speakerActive() = activeRoute

        override fun openRecord(): String {
            operations.add("open_record")
            return "capture"
        }

        override fun recordReady(record: String) = ready

        override fun registerRecord(record: String) {
            operations.add("register_record")
            if (failRegistration) throw IllegalStateException("registration")
        }

        override fun attachEffect(record: String): HandsFreeNativeAecEffect? {
            operations.add("attach_effect")
            if (!effectAvailable) return null
            return object : HandsFreeNativeAecEffect {
                override fun active() = effectActive
                override fun close() {
                    operations.add("release_effect")
                    if (failEffectRelease) throw IllegalStateException("release")
                }
            }
        }

        override fun recordingIssue(record: String) = recordingFailure
        override fun releaseRecord(record: String) { operations.add("release_record") }
        override fun releaseSpeaker() { operations.add("release_speaker"); activeRoute = false }
        override fun releaseMode() { operations.add("release_mode"); activeMode = false }
        override fun playbackGain() = gain
        override fun volume() = 7
    }

    @Test fun existingRecordingPreventsAllNativeRequests() {
        val port = Port()
        val reasons = mutableListOf<String>()
        val lease = HandsFreeNativeAecLease.tryOpen(port, { true }, reasons::add)
        assertNull(lease)
        assertEquals(listOf("native_aec_microphone_blocked"), reasons)
        assertTrue(port.operations.isEmpty())
    }

    @Test fun unsupportedRouteAllowsPortableFallbackWithoutRequestingMode() {
        val port = Port().apply { availability = "native_aec_non_speaker_route" }
        val reasons = mutableListOf<String>()
        assertNull(HandsFreeNativeAecLease.tryOpen(port, { false }, reasons::add))
        assertEquals(listOf("native_aec_non_speaker_route"), reasons)
        assertTrue(port.operations.isEmpty())
    }

    @Test fun failedModeAndRouteRequestsReleaseEachRequestedResource() {
        val mode = Port().apply { modeAvailable = false }
        val route = Port().apply { routeAvailable = false }
        assertNull(HandsFreeNativeAecLease.tryOpen(mode, { false }, {}))
        assertEquals(listOf("request_mode", "release_mode"), mode.operations)
        assertNull(HandsFreeNativeAecLease.tryOpen(route, { false }, {}))
        assertEquals(listOf("request_mode", "request_speaker", "release_speaker", "release_mode"),
            route.operations)
    }

    @Test fun competingRecordingAfterModeRequestAbortsAndRestoresMode() {
        val port = Port()
        val reasons = mutableListOf<String>()
        assertNull(HandsFreeNativeAecLease.tryOpen(port, { communication -> communication }, reasons::add))
        assertEquals(listOf("native_aec_microphone_blocked"), reasons)
        assertEquals(listOf("request_mode", "release_mode"), port.operations)
    }

    @Test fun nominalVolumeMismatchRestoresRouteBeforePortableFallback() {
        val port = Port().apply { gain = null }
        val reasons = mutableListOf<String>()
        assertNull(HandsFreeNativeAecLease.tryOpen(port, { false }, reasons::add))
        assertEquals(listOf("native_aec_gain_unavailable"), reasons)
        assertEquals(listOf("request_mode", "request_speaker", "release_speaker", "release_mode"),
            port.operations)
    }

    @Test fun badRecorderAndRegistrationFailureBothReleaseRecord() {
        val unready = Port().apply { ready = false }
        val error = Port().apply { failRegistration = true }
        for (port in listOf(unready, error)) {
            assertNull(HandsFreeNativeAecLease.tryOpen(port, { false }, {}))
            assertEquals(listOf("release_record", "release_speaker", "release_mode"),
                port.operations.takeLast(3))
        }
    }

    @Test fun missingOrInactiveEffectCannotClaimNativeAec() {
        val unavailable = Port().apply { effectAvailable = false }
        val inactive = Port().apply { effectActive = false }
        for (port in listOf(unavailable, inactive)) {
            assertNull(HandsFreeNativeAecLease.tryOpen(port, { false }, {}))
            assertEquals(listOf("release_record", "release_speaker", "release_mode"),
                port.operations.takeLast(3))
        }
        assertTrue("release_effect" in inactive.operations)
        assertFalse("release_effect" in unavailable.operations)
    }

    @Test fun recorderCloseDoesNotChangeRoutingDuringPlaybackAndFullCloseIsIdempotent() {
        val port = Port()
        val lease = checkNotNull(HandsFreeNativeAecLease.tryOpen(port, { false }, {}))
        assertEquals("capture", lease.record)
        assertEquals(7, lease.mediaVolume())
        assertEquals(0.5f, lease.playbackGain(), 0f)
        assertTrue(lease.usable())
        lease.closeRecorder()
        lease.closeRecorder()
        assertFalse(lease.usable())
        assertEquals("native_aec_recorder_closed", lease.unavailableReason())
        assertTrue(lease.isOwnedMode())
        assertTrue(port.activeRoute)
        lease.close()
        lease.close()
        assertFalse(lease.isOwnedMode())
        assertFalse(port.activeRoute)
        assertEquals(listOf("request_mode", "request_speaker", "open_record", "register_record",
            "attach_effect", "release_effect", "release_record", "release_speaker", "release_mode"),
            port.operations)
    }

    @Test fun routeModeEffectSilencingAndCompetingCaptureInvalidateNativeLease() {
        val port = Port()
        var competing = false
        val lease = checkNotNull(HandsFreeNativeAecLease.tryOpen(port, { competing }, {}))
        port.effectActive = false
        assertEquals("native_aec_effect_lost", lease.unavailableReason())
        port.effectActive = true
        port.recordingFailure = "native_aec_recorder_silenced"
        assertEquals("native_aec_recorder_silenced", lease.unavailableReason())
        port.recordingFailure = null
        competing = true
        assertEquals("native_aec_microphone_blocked", lease.unavailableReason())
        competing = false
        port.activeRoute = false
        assertEquals("native_aec_route_changed", lease.unavailableReason())
        port.activeRoute = true
        port.activeMode = false
        assertEquals("native_aec_mode_lost", lease.unavailableReason())
        lease.close()
        assertEquals(listOf("release_speaker", "release_mode"), port.operations.takeLast(2))
    }

    @Test fun effectCleanupExceptionStillReleasesRecorderAndRestoresRoute() {
        val port = Port().apply { failEffectRelease = true }
        val lease = checkNotNull(HandsFreeNativeAecLease.tryOpen(port, { false }, {}))
        try {
            lease.close()
            fail("cleanup failure must be visible")
        } catch (_: IllegalStateException) {
            assertEquals(listOf("release_effect", "release_record", "release_speaker", "release_mode"),
                port.operations.takeLast(4))
            assertFalse(lease.isOwnedMode())
        }
    }

    @Test fun nominalAttenuationCannotBoostOrIgnoreMutedOrUnknownMediaLevel() {
        assertEquals(1f, checkNotNull(nativeAecAttenuation(-10f, -10f)), 0f)
        assertEquals(0.501187f, checkNotNull(nativeAecAttenuation(-16f, -10f)), 0.000001f)
        assertNull(nativeAecAttenuation(-8f, -10f))
        assertNull(nativeAecAttenuation(Float.NEGATIVE_INFINITY, -10f))
        assertNull(nativeAecAttenuation(-10f, Float.NaN))
    }
}
