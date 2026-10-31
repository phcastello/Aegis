package com.aegis.node

import android.content.Context
import android.os.Bundle
import android.service.voice.VoiceInteractionSession
import android.service.voice.VoiceInteractionSessionService
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView

class NodeVoiceInteractionSessionService:VoiceInteractionSessionService() {
    override fun onNewSession(args:Bundle?):VoiceInteractionSession=NodeVoiceInteractionSession(this)
}

/** Small system assistant panel over the lock screen/other apps. Never reads assist content. */
class NodeVoiceInteractionSession(context:Context):VoiceInteractionSession(context) {
    private var label:TextView?=null
    private var closing=false
    private var boundSession:String?=null
    override fun onCreateContentView():View {
        window?.window?.apply{addFlags(WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED);clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);setGravity(Gravity.BOTTOM)}
        val panel=LinearLayout(context).apply{orientation=LinearLayout.VERTICAL;setPadding(28,18,28,18);setBackgroundColor(0xff18202b.toInt())}
        label=TextView(context).apply{text="Aegis";setTextColor(0xfff4f6fb.toInt());textSize=18f;setPadding(0,0,0,12)}.also{panel.addView(it)}
        panel.addView(Button(context).apply{text="Cancelar";setOnClickListener{boundSession?.let{NodeHandsFree.runtime(context).cancelSession(it)};finishQuietly()}})
        return panel
    }
    override fun onShow(args:Bundle?,showFlags:Int) {
        closing=false;current=this;super.onShow(args,showFlags)
        boundSession=if(args?.getBoolean("activation")==true)args.getString("sessionId") else NodeHandsFree.runtime(context).activateFromAssistant()
        val state=NodeHandsFree.runtime(context).state();refresh(state.optString("status"),state.optString("message"))
    }
    internal fun refresh(state:String,message:String){label?.text=message}
    internal fun finishIfSession(token:String){if(boundSession==token)finishQuietly()}
    internal fun finishQuietly(){closing=true;if(current===this)current=null;finish()}
    override fun onHide(){if(!closing && current===this)boundSession?.let{NodeHandsFree.runtime(context).cancelSession(it)};if(current===this)current=null;super.onHide()}
    override fun onDestroy(){if(!closing && current===this)boundSession?.let{NodeHandsFree.runtime(context).cancelSession(it)};if(current===this)current=null;super.onDestroy()}
    companion object {@Volatile internal var current:NodeVoiceInteractionSession?=null}
}
