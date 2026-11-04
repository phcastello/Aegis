package com.aegis.node

import android.animation.ValueAnimator
import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.Settings
import android.service.voice.VoiceInteractionSession
import android.service.voice.VoiceInteractionSessionService
import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.view.accessibility.AccessibilityManager
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import kotlin.math.abs
import kotlin.math.sin

class NodeVoiceInteractionSessionService:VoiceInteractionSessionService() {
    override fun onNewSession(args:Bundle?):VoiceInteractionSession=NodeVoiceInteractionSession(this)
}

/** Disposable official assistant panel. Runtime capture and playback own their lifecycle. */
class NodeVoiceInteractionSession(context:Context):VoiceInteractionSession(context) {
    private val main=Handler(Looper.getMainLooper())
    private var label:TextView?=null
    private var partial:TextView?=null
    private var card:View?=null
    private var visualizer:HandsFreePanelView?=null
    private var visible=false
    private var boundSession:String?=null
    private var displayedState=""
    private var partialRequestId:String?=null
    private val presentation=HandsFreePanelPresentation()
    private val dismissError=Runnable{finishQuietly()}
    private fun dp(value:Int)=(value*context.resources.displayMetrics.density+0.5f).toInt()
    override fun onCreateContentView():View {
        val root=FrameLayout(context).apply{setPadding(dp(12),dp(8),dp(12),dp(12))}
        val panel=LinearLayout(context).apply{
            orientation=LinearLayout.VERTICAL
            setPadding(dp(20),dp(8),dp(12),dp(20))
            background=GradientDrawable().apply{setColor(0xff172132.toInt());cornerRadius=dp(28).toFloat();setStroke(dp(1),0xff35435b.toInt())}
        }
        card=panel
        val header=LinearLayout(context).apply{orientation=LinearLayout.HORIZONTAL;gravity=Gravity.CENTER_VERTICAL}
        header.addView(TextView(context).apply{
            text="Aegis";setTextColor(0xffbdcbf6.toInt());textSize=15f;gravity=Gravity.CENTER_VERTICAL
            typeface=android.graphics.Typeface.create("sans-serif-medium",android.graphics.Typeface.NORMAL)
        },LinearLayout.LayoutParams(0,dp(48),1f))
        header.addView(Button(context).apply{
            text="×";contentDescription="Cancelar interação";textSize=27f;setTextColor(0xffbdcbf6.toInt())
            minWidth=0;minimumWidth=0;minHeight=0;minimumHeight=0;setPadding(0,0,0,0)
            background=android.graphics.drawable.StateListDrawable().apply{
                addState(intArrayOf(android.R.attr.state_pressed),GradientDrawable().apply{shape=GradientDrawable.OVAL;setColor(0xff35435b.toInt())})
                addState(intArrayOf(),GradientDrawable().apply{shape=GradientDrawable.OVAL;setColor(0xff253248.toInt())})
            }
            setOnClickListener{boundSession?.let{NodeHandsFree.runtime(context).cancelSession(it)};finishQuietly()}
        },LinearLayout.LayoutParams(dp(48),dp(48)))
        panel.addView(header)
        visualizer=HandsFreePanelView(context).also{panel.addView(it,LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT,dp(60)))}
        label=TextView(context).apply{
            text=presentation.state.label;setTextColor(0xffdbe3f4.toInt());textSize=14f;gravity=Gravity.CENTER
            setPadding(0,dp(4),dp(8),0);accessibilityLiveRegion=View.ACCESSIBILITY_LIVE_REGION_POLITE
        }.also{panel.addView(it,LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT,LinearLayout.LayoutParams.WRAP_CONTENT))}
        partial=TextView(context).apply{
            textSize=14f;setTextColor(0xffaab5ca.toInt());gravity=Gravity.CENTER;maxLines=2;ellipsize=TextUtils.TruncateAt.END
            visibility=View.GONE;setPadding(0,dp(8),dp(8),0);contentDescription="Transcrição temporária"
            accessibilityLiveRegion=View.ACCESSIBILITY_LIVE_REGION_NONE
        }.also{panel.addView(it)}
        root.addView(panel,FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT,FrameLayout.LayoutParams.WRAP_CONTENT,Gravity.BOTTOM))
        ViewCompat.setOnApplyWindowInsetsListener(root){view,insets->
            val safe=insets.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout())
            view.setPadding(dp(12)+safe.left,dp(8),dp(12)+safe.right,dp(12)+safe.bottom)
            insets
        }
        ViewCompat.requestApplyInsets(root)
        return root
    }
    override fun onShow(args:Bundle?,showFlags:Int) {
        main.removeCallbacks(dismissError)
        visible=true;super.onShow(args,showFlags)
        val runtime=NodeHandsFree.runtime(context)
        val previousSession=boundSession
        boundSession=if(args?.getBoolean("activation")==true)args.getString("sessionId") else runtime.activateFromAssistant() ?: runtime.activeSessionToken()
        if(args?.getBoolean("activation")==true && boundSession!=runtime.activeSessionToken()){finishQuietly();return}
        if(previousSession!=boundSession){displayedState="";partialRequestId=null;partial?.text="";partial?.visibility=View.GONE}
        presentation.begin(boundSession)
        current=this
        val decision=boundSession?.let{NodeVoiceInteractionService.current?.presentationDecision(it,args?.getBoolean("wakeScreen")==true || runtime.activationNeedsWake(it))}
            ?: HandsFreePresentationPolicy.Decision(false,false)
        // AOSP creates MATCH_PARENT before onShow. Keep the real window at the bottom,
        // including the lock screen, without dimming or keeping the display awake.
        window?.window?.apply {
            addFlags(WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL)
            clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or WindowManager.LayoutParams.FLAG_DIM_BEHIND or WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)
            // Preserve the existing one-shot activation request; never hold the screen on.
            if(decision.wakeScreen)addFlags(WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)
            setBackgroundDrawableResource(android.R.color.transparent)
            setGravity(Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL)
            setLayout(minOf(context.resources.displayMetrics.widthPixels,dp(520)),WindowManager.LayoutParams.WRAP_CONTENT)
            decorView.post{clearFlags(WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)}
        }
        HandsFreePanelSignal.attach(this,boundSession);visualizer?.show(this)
        val state=runtime.state();refresh(state.optString("status"),state.optString("message"))
    }
    override fun onComputeInsets(outInsets:Insets) {
        super.onComputeInsets(outInsets)
        outInsets.touchableInsets=Insets.TOUCHABLE_INSETS_REGION
        val view=card;val location=IntArray(2)
        if(view!=null && view.width>0){view.getLocationInWindow(location);outInsets.touchableRegion.set(location[0],location[1],location[0]+view.width,location[1]+view.height)}
        else outInsets.touchableRegion.setEmpty()
    }
    internal fun refresh(state:String,@Suppress("UNUSED_PARAMETER") message:String) {
        if(!presentation.refresh(state))return
        if(displayedState!=state){partial?.text="";partial?.visibility=View.GONE;partialRequestId=null;HandsFreePanelSignal.reset(this)}
        displayedState=state
        label?.text=presentation.state.label;visualizer?.state(presentation.state)
    }
    internal fun showPartial(token:String,requestId:String,text:String) {
        if(boundSession!=token || !visible || displayedState !in setOf("capturing","continuation"))return
        if(partialRequestId==null)partialRequestId=requestId
        if(partialRequestId!=requestId)return
        val value=text.take(HandsFreePartialDisplayLimiter.MAX_CHARACTERS)
        partial?.text=value;partial?.visibility=if(value.isEmpty())View.GONE else View.VISIBLE
    }
    internal fun suppressScreenOn(){releaseWindowFlags()}
    private fun releaseWindowFlags(){window?.window?.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)}
    internal fun finishIfSession(token:String,immediate:Boolean=false,failed:Boolean=false) {
        if(boundSession!=token)return
        if(presentation.delayClose(immediate,failed)){
            partial?.text="";partial?.visibility=View.GONE;HandsFreePanelSignal.reset(this)
            label?.text=presentation.state.label;visualizer?.state(presentation.state)
            main.removeCallbacks(dismissError);main.postDelayed(dismissError,3500)
        } else finishQuietly()
    }
    internal fun cancellationSnapshot():Runnable? {
        val snapshot=presentation.snapshot()?:return null
        return Runnable{if(current===this && presentation.isCurrent(snapshot))finishQuietly()}
    }
    internal fun finishQuietly(){visible=false;stopVisuals();releaseWindowFlags();partial?.text="";if(current===this)current=null;finish()}
    private fun stopVisuals(){presentation.end();main.removeCallbacks(dismissError);HandsFreePanelSignal.detach(this);visualizer?.hide()}
    override fun onHide(){visible=false;stopVisuals();releaseWindowFlags();partial?.text="";if(current===this)current=null;super.onHide()}
    override fun onDestroy(){visible=false;stopVisuals();releaseWindowFlags();partial?.text="";if(current===this)current=null;super.onDestroy()}
    companion object {@Volatile internal var current:NodeVoiceInteractionSession?=null}
}

/** 30fps at most, only while shown. Playback dots are a state animation, not an acoustic meter. */
internal class HandsFreePanelView(context:Context):View(context) {
    private val paint=Paint(Paint.ANTI_ALIAS_FLAG)
    private val bounds=RectF()
    private val density=resources.displayMetrics.density
    private val accessibility=context.getSystemService(AccessibilityManager::class.java)
    private var shown=false
    private var owner:Any?=null
    private var state=HandsFreePanelState.IDLE
    private var level=0f
    private var activatedAt=0L
    private var reduceMotion=true
    private val motionChanged=AccessibilityManager.AccessibilityStateChangeListener{updateMotion()}
    private val touchExplorationChanged=AccessibilityManager.TouchExplorationStateChangeListener{updateMotion()}
    private val frame=Runnable{invalidate()}
    init {importantForAccessibility=IMPORTANT_FOR_ACCESSIBILITY_NO}
    internal fun show(observer:Any) {
        if(shown)hide()
        owner=observer;shown=true;activatedAt=SystemClock.elapsedRealtime();level=0f
        accessibility.addAccessibilityStateChangeListener(motionChanged)
        accessibility.addTouchExplorationStateChangeListener(touchExplorationChanged)
        updateMotion();invalidate()
    }
    internal fun hide() {
        shown=false;level=0f;removeCallbacks(frame)
        accessibility.removeAccessibilityStateChangeListener(motionChanged)
        accessibility.removeTouchExplorationStateChangeListener(touchExplorationChanged)
    }
    internal fun state(next:HandsFreePanelState){if(state!=next)level=0f;state=next;removeCallbacks(frame);if(shown)invalidate()}
    private fun updateMotion() {
        val animations=if(Build.VERSION.SDK_INT>=26)ValueAnimator.areAnimatorsEnabled()
            else Settings.Global.getFloat(context.contentResolver,Settings.Global.ANIMATOR_DURATION_SCALE,1f)>0f
        reduceMotion=!animations || accessibility.isEnabled
        if(shown)invalidate()
    }
    override fun onAttachedToWindow(){super.onAttachedToWindow();if(shown)invalidate()}
    override fun onDetachedFromWindow(){hide();super.onDetachedFromWindow()}
    override fun onWindowVisibilityChanged(visibility:Int){super.onWindowVisibilityChanged(visibility);if(visibility!=VISIBLE)removeCallbacks(frame) else if(shown)invalidate()}
    override fun onDraw(canvas:Canvas) {
        super.onDraw(canvas)
        removeCallbacks(frame)
        if(!shown || windowVisibility!=VISIBLE)return
        // Re-read animation scale each visible frame so a system reduce-motion change takes effect.
        val animations=if(Build.VERSION.SDK_INT>=26)ValueAnimator.areAnimatorsEnabled() else !reduceMotion
        val motion=!reduceMotion && animations
        val now=SystemClock.elapsedRealtime()
        paint.color=0xff91a9ee.toInt()
        val centerX=width/2f;val centerY=height/2f
        when(state) {
            HandsFreePanelState.CAPTURING,HandsFreePanelState.CONTINUATION->{
                val target=HandsFreePanelSignal.level(owner,now)
                level=if(motion)level+(target-level)*if(target>level)0.48f else 0.2f else target
                val gap=6f*density;val bar=5f*density
                for(index in 0..8) {
                    val weight=1f-abs(index-4)*0.14f
                    val half=(3f+level*21f*weight)*density
                    val x=centerX+(index-4)*(bar+gap)
                    bounds.set(x-bar/2,centerY-half,x+bar/2,centerY+half)
                    canvas.drawRoundRect(bounds,bar/2,bar/2,paint)
                }
            }
            HandsFreePanelState.PROCESSING,HandsFreePanelState.SPEAKING->{
                for(index in 0..2) {
                    val phase=(now%1500)/1500f*6.283185f-index*0.9f
                    val pulse=if(motion)(sin(phase)+1f)/2 else 0.5f
                    paint.alpha=(110+pulse*145).toInt()
                    canvas.drawCircle(centerX+(index-1)*18f*density,centerY,(3.5f+pulse)*density,paint)
                }
                paint.alpha=255
            }
            HandsFreePanelState.ERROR->{
                paint.color=0xfff2b5b5.toInt();paint.textSize=27f*density;paint.textAlign=Paint.Align.CENTER
                canvas.drawText("!",centerX,centerY+9f*density,paint)
            }
            else->{canvas.drawCircle(centerX,centerY,5f*density,paint)}
        }
        val activationAge=now-activatedAt
        if(motion && activationAge<260 && state!=HandsFreePanelState.ERROR) {
            paint.style=Paint.Style.STROKE;paint.strokeWidth=1.5f*density;paint.alpha=((1f-activationAge/260f)*150).toInt()
            canvas.drawCircle(centerX,centerY,(12f+activationAge/260f*17f)*density,paint)
            paint.style=Paint.Style.FILL;paint.alpha=255
        }
        // Captured amplitude still updates under reduced motion; only decorative/easing motion stops.
        val interval=if(motion)34L else 200L
        if(state.animated && (motion || state==HandsFreePanelState.CAPTURING || state==HandsFreePanelState.CONTINUATION))postDelayed(frame,interval)
    }
}
