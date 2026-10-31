package com.aegis.node

import android.content.Intent
import android.speech.RecognitionService
import android.speech.SpeechRecognizer

/** Android requires this metadata component. Real STT remains the existing private backend. */
class NodeRecognitionService:RecognitionService() {
    override fun onStartListening(intent:Intent,listener:Callback){listener.error(SpeechRecognizer.ERROR_CLIENT)}
    override fun onCancel(listener:Callback){}
    override fun onStopListening(listener:Callback){}
}
