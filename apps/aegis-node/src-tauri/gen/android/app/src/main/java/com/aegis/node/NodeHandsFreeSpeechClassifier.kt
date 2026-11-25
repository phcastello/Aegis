package com.aegis.node

/** Small WebRTC VAD instance, used during command capture/continuation, never continuous STT. */
internal class NativeHandsFreeSpeechClassifier:HandsFreeSpeechClassifier,AutoCloseable {
    private var handle=HandsFreeSpeechVadNative.create().also{check(it!=0L){"vad_unavailable"}}
    override fun isSpeech(frame:ShortArray):Boolean {
        check(handle!=0L);require(frame.size==HandsFreeVad.FRAME_SAMPLES)
        return when(HandsFreeSpeechVadNative.process(handle,frame)) {
            0->false;1->true;else->error("vad_process")
        }
    }
    fun reset(){check(handle!=0L && HandsFreeSpeechVadNative.reset(handle)){"vad_reset"}}
    override fun close(){val active=handle;handle=0;if(active!=0L)HandsFreeSpeechVadNative.destroy(active)}
}
internal object HandsFreeSpeechVadNative {
    init {System.loadLibrary("aegis_microwakeword")}
    external fun create():Long
    external fun destroy(handle:Long)
    external fun reset(handle:Long):Boolean
    external fun process(handle:Long,frame:ShortArray):Int
}
