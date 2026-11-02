package com.aegis.node

import android.content.Context
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.service.voice.VoiceInteractionSession
import android.service.voice.VoiceInteractionSessionService
import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.ImageButton
import android.widget.LinearLayout
import android.widget.TextView
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat

class NodeVoiceInteractionSessionService:VoiceInteractionSessionService() {
    override fun onNewSession(args:Bundle?):VoiceInteractionSession=NodeVoiceInteractionSession(this)
}

/** The window is disposable presentation. Runtime capture, requests and playback own their lifecycle. */
class NodeVoiceInteractionSession(context:Context):VoiceInteractionSession(context) {
    private var label:TextView?=null
    private var partial:TextView?=null
    private var card:View?=null
    private var boundSession:String?=null
    private var visible=false
    private var screenSuppressed=false
    private var displayedState=""
    private var partialRequestId:String?=null
    private fun dp(value:Int)=(value*context.resources.displayMetrics.density).toInt()
    override fun onCreateContentView():View {
        val root=FrameLayout(context).apply{setPadding(dp(12),dp(8),dp(12),dp(8))}
        val panel=LinearLayout(context).apply {
            orientation=LinearLayout.VERTICAL;setPadding(dp(16),dp(10),dp(8),dp(10))
            background=GradientDrawable().apply{setColor(0xe6232b36.toInt());cornerRadius=dp(24).toFloat()}
            elevation=dp(4).toFloat()
        }
        card=panel
        val row=LinearLayout(context).apply{orientation=LinearLayout.HORIZONTAL;gravity=Gravity.CENTER_VERTICAL}
        row.addView(TextView(context).apply{text="●";textSize=15f;setTextColor(0xffb0c9c1.toInt());importantForAccessibility=View.IMPORTANT_FOR_ACCESSIBILITY_NO;setPadding(0,0,dp(10),0)})
        label=TextView(context).apply {
            text="Aegis";setTextColor(0xfff4f6fb.toInt());textSize=16f;maxLines=1;ellipsize=TextUtils.TruncateAt.END
        }.also{row.addView(it,LinearLayout.LayoutParams(0,LinearLayout.LayoutParams.WRAP_CONTENT,1f))}
        row.addView(ImageButton(context).apply {
            setImageResource(android.R.drawable.ic_menu_close_clear_cancel);imageTintList=android.content.res.ColorStateList.valueOf(0xffc5cbd2.toInt())
            contentDescription="Cancelar pedido";setBackgroundColor(Color.TRANSPARENT);setPadding(dp(12),dp(12),dp(12),dp(12))
            setOnClickListener{boundSession?.let{NodeHandsFree.runtime(context).cancelSession(it)};finishQuietly()}
        },LinearLayout.LayoutParams(dp(48),dp(48)))
        panel.addView(row)
        partial=TextView(context).apply {
            textSize=14f;setTextColor(0xffc5cbd2.toInt());maxLines=2;ellipsize=TextUtils.TruncateAt.END
            visibility=View.GONE;setPadding(dp(25),0,dp(12),dp(4));contentDescription="Transcrição temporária"
            // Updates are intentionally not announced on every streaming token.
            accessibilityLiveRegion=View.ACCESSIBILITY_LIVE_REGION_NONE
        }.also{panel.addView(it)}
        root.addView(panel,FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT,FrameLayout.LayoutParams.WRAP_CONTENT,Gravity.TOP))
        ViewCompat.setOnApplyWindowInsetsListener(root){view,insets->
            val safe=insets.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout())
            view.setPadding(dp(12)+safe.left,dp(8)+safe.top,dp(12)+safe.right,dp(8))
            insets
        }
        ViewCompat.requestApplyInsets(root)
        return root
    }
    override fun onShow(args:Bundle?,showFlags:Int) {
        visible=true;super.onShow(args,showFlags)
        val runtime=NodeHandsFree.runtime(context)
        boundSession=if(args?.getBoolean("activation")==true)args.getString("sessionId") else runtime.activateFromAssistant() ?: runtime.activeSessionToken()
        if(args?.getBoolean("activation")==true && boundSession!=runtime.activeSessionToken()) {finishQuietly();return}
        current=this
        val decision=boundSession?.let{NodeVoiceInteractionService.current?.presentationDecision(it,args?.getBoolean("wakeScreen")==true || runtime.activationNeedsWake(it))}
            ?: HandsFreePresentationPolicy.Decision(false,false)
        screenSuppressed=!decision.keepScreenOn
        window?.window?.apply {
            // VoiceInteractionSession has no Activity.setTurnScreenOn equivalent. The public
            // window flag is a one-shot best-effort request; it never dismisses the keyguard.
            addFlags(WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL)
            clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or WindowManager.LayoutParams.FLAG_DIM_BEHIND or WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)
            if(decision.wakeScreen)addFlags(WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)
            setBackgroundDrawableResource(android.R.color.transparent)
            setGravity(Gravity.TOP or Gravity.CENTER_HORIZONTAL)
            setLayout(minOf(context.resources.displayMetrics.widthPixels,dp(440)),WindowManager.LayoutParams.WRAP_CONTENT)
            decorView.post{clearFlags(WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)}
        }
        val state=runtime.state();refresh(state.optString("status"),state.optString("message"))
    }
    override fun onComputeInsets(outInsets:Insets) {
        super.onComputeInsets(outInsets)
        outInsets.touchableInsets=Insets.TOUCHABLE_INSETS_REGION
        val view=card;val location=IntArray(2)
        if(view!=null && view.width>0){view.getLocationInWindow(location);outInsets.touchableRegion.set(location[0],location[1],location[0]+view.width,location[1]+view.height)}
        else outInsets.touchableRegion.setEmpty()
    }
    internal fun refresh(state:String,message:String) {
        if(displayedState!=state){partial?.text="";partial?.visibility=View.GONE;partialRequestId=null}
        displayedState=state
        label?.text=when(state){
            "capturing"->"Aegis · Escutando"
            "processing"->"Aegis · Processando"
            "speaking"->"Aegis · Falando"
            "continuation"->"Aegis · Aguardando continuação"
            else->message
        }
        val active=state in setOf("capturing","processing","speaking","continuation") && boundSession!=null
        window?.window?.apply{if(visible && active && !screenSuppressed)addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON) else clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)}
    }
    internal fun showPartial(token:String,requestId:String,text:String) {
        // requestId is already validated by the realtime transport. The runtime token keeps
        // late main-thread callbacks from a previous session out of a new panel.
        if(boundSession!=token || !visible || displayedState !in setOf("capturing","continuation"))return
        if(partialRequestId==null)partialRequestId=requestId
        if(partialRequestId!=requestId)return
        val value=text.take(HandsFreePartialDisplayLimiter.MAX_CHARACTERS)
        partial?.text=value;partial?.visibility=if(value.isEmpty())View.GONE else View.VISIBLE
    }
    internal fun suppressScreenOn(){screenSuppressed=true;releaseWindowFlags()}
    private fun releaseWindowFlags(){window?.window?.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)}
    internal fun finishIfSession(token:String){if(boundSession==token)finishQuietly()}
    internal fun finishQuietly(){visible=false;releaseWindowFlags();partial?.text="";if(current===this)current=null;finish()}
    override fun onHide(){visible=false;releaseWindowFlags();partial?.text="";if(current===this)current=null;super.onHide()}
    override fun onDestroy(){visible=false;releaseWindowFlags();partial?.text="";if(current===this)current=null;super.onDestroy()}
    companion object {@Volatile internal var current:NodeVoiceInteractionSession?=null}
}
