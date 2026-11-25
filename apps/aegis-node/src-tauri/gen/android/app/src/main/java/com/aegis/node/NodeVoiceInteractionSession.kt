package com.aegis.node

import android.animation.ValueAnimator
import android.content.Context
import android.database.ContentObserver
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
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import java.util.concurrent.atomic.AtomicBoolean

class NodeVoiceInteractionSessionService:VoiceInteractionSessionService() {
    override fun onNewSession(args:Bundle?):VoiceInteractionSession=NodeVoiceInteractionSession(this)
}

/** The explicit system scale wins over ValueAnimator's asynchronously updated cache. */
internal fun handsFreePanelMotionAllowed(scale:Float,platformEnabled:Boolean)=
    if(scale.isFinite())scale>0f else platformEnabled

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
            setPadding(dp(20),dp(4),dp(12),dp(16))
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
        visualizer=HandsFreePanelView(context).also{panel.addView(it,LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT,dp(72)))}
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
        updatePlaybackAccessibility(null)
        current=this
        val decision=boundSession?.let{NodeVoiceInteractionService.current?.presentationDecision(it,args?.getBoolean("wakeScreen")==true || runtime.activationNeedsWake(it))}
            ?: HandsFreePresentationPolicy.Decision(false,false)
        // AOSP creates MATCH_PARENT before onShow. Keep the real window at the bottom,
        // including the lock screen, without dimming. The active-session display
        // timeout policy is applied after status refresh below.
        window?.window?.apply {
            addFlags(WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL)
            clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or WindowManager.LayoutParams.FLAG_DIM_BEHIND or WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)
            // Preserve the existing one-shot wake separately from timeout prevention.
            if(decision.wakeScreen)addFlags(WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)
            setBackgroundDrawableResource(android.R.color.transparent)
            setGravity(Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL)
            setLayout(minOf(context.resources.displayMetrics.widthPixels,dp(520)),WindowManager.LayoutParams.WRAP_CONTENT)
            decorView.post{clearFlags(WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)}
        }
        visualizer?.show(this,boundSession)
        val state=runtime.state();refresh(state.optString("status"),state.optString("message"))
        updateScreenPolicy()
    }
    override fun onComputeInsets(outInsets:Insets) {
        super.onComputeInsets(outInsets)
        outInsets.touchableInsets=Insets.TOUCHABLE_INSETS_REGION
        val view=card;val location=IntArray(2)
        if(view!=null && view.width>0){view.getLocationInWindow(location);outInsets.touchableRegion.set(location[0],location[1],location[0]+view.width,location[1]+view.height)}
        else outInsets.touchableRegion.setEmpty()
    }
    internal fun refresh(state:String,message:String) {
        if(!presentation.refresh(state))return
        if(displayedState!=state){partial?.text="";partial?.visibility=View.GONE;partialRequestId=null;HandsFreePanelSignal.reset(this)}
        displayedState=state
        label?.text=presentation.state.label;visualizer?.state(presentation.state)
        val token = boundSession
        if (state == "speaking" && token != null) refreshBargeInControls(token)
        else updatePlaybackAccessibility(null)
        updateScreenPolicy()
    }

    internal fun refreshBargeInControls(token: String) {
        if (!visible || boundSession != token || presentation.state != HandsFreePanelState.SPEAKING) return
        val availability = NodeHandsFree.runtime(context).bargeInPanelAvailability(token)
        updatePlaybackAccessibility(presentation.bargeInAccessibilityHint(token, availability))
    }

    private fun updatePlaybackAccessibility(hint: String?) {
        // Availability remains readable via TalkBack without an extra visible instruction.
        label?.contentDescription = hint?.let { "${presentation.state.label}. $it" }
    }

    internal fun showPartial(token:String,requestId:String,text:String) {
        if(boundSession!=token || !visible || displayedState !in setOf("capturing","continuation"))return
        if(partialRequestId==null)partialRequestId=requestId
        if(partialRequestId!=requestId)return
        val value=text.take(HandsFreePartialDisplayLimiter.MAX_CHARACTERS)
        partial?.text=value;partial?.visibility=if(value.isEmpty())View.GONE else View.VISIBLE
    }
    // A screen-off broadcast cancels the one-shot wake request, never re-wakes
    // the device. KEEP_SCREEN_ON only prevents timeout while our window is visible.
    internal fun suppressScreenOn(){window?.window?.clearFlags(WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)}
    private fun updateScreenPolicy() {
        val keep=handsFreePanelKeepsScreenOn(presentation.state,visible,boundSession!=null)
        window?.window?.let {
            if(keep)it.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
            else it.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
    }
    private fun releaseWindowFlags(){window?.window?.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)}
    internal fun finishIfSession(token:String,immediate:Boolean=false,failed:Boolean=false) {
        if(boundSession!=token)return
        if(presentation.delayClose(immediate,failed)){
            partial?.text="";partial?.visibility=View.GONE;HandsFreePanelSignal.reset(this)
            updatePlaybackAccessibility(null)
            label?.text=presentation.state.label;visualizer?.state(presentation.state)
            updateScreenPolicy()
            main.removeCallbacks(dismissError);main.postDelayed(dismissError,3500)
        } else finishQuietly()
    }
    internal fun cancellationSnapshot():Runnable? {
        val snapshot=presentation.snapshot()?:return null
        return Runnable{if(current===this && presentation.isCurrent(snapshot))finishQuietly()}
    }
    internal fun finishQuietly(){visible=false;stopVisuals();releaseWindowFlags();partial?.text="";if(current===this)current=null;finish()}
    private fun stopVisuals(){presentation.end();updatePlaybackAccessibility(null);main.removeCallbacks(dismissError);HandsFreePanelSignal.detach(this);visualizer?.hide()}
    override fun onHide(){visible=false;stopVisuals();releaseWindowFlags();partial?.text="";if(current===this)current=null;super.onHide()}
    override fun onDestroy(){visible=false;stopVisuals();releaseWindowFlags();partial?.text="";if(current===this)current=null;super.onDestroy()}
    companion object {@Volatile internal var current:NodeVoiceInteractionSession?=null}
}

/** Vsync presentation of measured input history and playback spectrum. */
internal class HandsFreePanelView(context:Context):View(context) {
    private var lifecycleReady=false
    private val paint=Paint(Paint.ANTI_ALIAS_FLAG)
    private val bounds=RectF()
    private val density=resources.displayMetrics.density
    private val main=Handler(Looper.getMainLooper())
    private val amplitudes=FloatArray(HandsFreePanelSignal.BAR_COUNT)
    private val playbackDisplay=HandsFreePlaybackSpectrum.Display()
    private val wakePosted=AtomicBoolean(false)
    @Volatile private var shown=false
    private var owner:Any?=null
    private var session:String?=null
    @Volatile private var signalBound=false
    private var scaleObserved=false
    private var framePending=false
    private var playbackFramePending=false
    // Reused drawing buffer: the approved three-dot motion is sampled in 28 x 28 coordinates.
    private val thoughtPositions=FloatArray(6)
    private var thoughtStartedAtMs=0L
    private var state=HandsFreePanelState.IDLE
    private var reduceMotion=true
    private val frame=Runnable{framePending=false;if(canRender())invalidate()}
    private val playbackFrame=Runnable{playbackFramePending=false;if(canRender() && state==HandsFreePanelState.SPEAKING)invalidate()}
    private val staleFrame=Runnable{if(canRender() && inputState()){amplitudes.fill(0f);invalidate()}}
    private val wakeFrame=Runnable{
        wakePosted.set(false)
        if(canRender() && state==HandsFreePanelState.SPEAKING) {
            schedulePlaybackFrame()
        }else if(canRender() && inputState()) {
            if(reduceMotion) {
                // Real data still updates at capture cadence when the owner removes motion.
                // A single expiry also clears a stalled producer; this is not a drawing loop.
                removeCallbacks(staleFrame)
                postDelayed(staleFrame,HandsFreePanelSignal.STALE_CLEAR_MS)
                invalidate()
            }else scheduleFrame()
        }
    }
    private val dataChanged:()->Unit={if(shown && signalBound && wakePosted.compareAndSet(false,true))post(wakeFrame)}
    private val scaleChanged=object:ContentObserver(main){override fun onChange(selfChange:Boolean){updateMotion()}}
    init {importantForAccessibility=IMPORTANT_FOR_ACCESSIBILITY_NO;lifecycleReady=true}
    internal fun show(observer:Any,token:String?) {
        hide()
        owner=observer;session=token;shown=true
        thoughtStartedAtMs=SystemClock.elapsedRealtime()
        resumeVisibleResources()
    }
    internal fun hide() {
        shown=false
        pauseVisibleResources()
        owner=null;session=null
    }
    internal fun state(next:HandsFreePanelState) {
        if(state!=next) {
            val now=SystemClock.elapsedRealtime()
            if(next==HandsFreePanelState.PROCESSING)thoughtStartedAtMs=now
            owner?.let {
                HandsFreePanelSignal.reset(it,now)
                HandsFreePlaybackSpectrum.phaseChanged(it,state,next)
            }
            amplitudes.fill(0f);playbackDisplay.clear()
        }
        state=next
        removeCallbacks(frame);framePending=false;removeCallbacks(staleFrame)
        removeCallbacks(playbackFrame);playbackFramePending=false
        if(canRender())invalidate()
    }
    private fun inputState()=state==HandsFreePanelState.CAPTURING || state==HandsFreePanelState.CONTINUATION
    private fun canRender()=shown && isAttachedToWindow && isShown && windowVisibility==VISIBLE
    private fun scheduleFrame() {
        if(canRender() && !framePending) {
            framePending=true
            // View animation callbacks run with display vsync and coalesce late visual frames.
            postOnAnimation(frame)
        }
    }
    private fun schedulePlaybackFrame() {
        if(canRender() && !playbackFramePending) {
            playbackFramePending=true
            postDelayed(playbackFrame,40L)
        }
    }
    private fun resumeVisibleResources() {
        if(!canRender())return
        if(!signalBound)owner?.let {
            HandsFreePanelSignal.attach(it,session)
            HandsFreePanelSignal.setListener(it,dataChanged)
            HandsFreePlaybackSpectrum.attach(it,session)
            HandsFreePlaybackSpectrum.setListener(it,dataChanged)
            signalBound=true
        }
        if(!scaleObserved)try {
            context.contentResolver.registerContentObserver(Settings.Global.getUriFor(Settings.Global.ANIMATOR_DURATION_SCALE),false,scaleChanged)
            scaleObserved=true
        }catch(_:RuntimeException){}
        updateMotion();invalidate()
    }
    private fun pauseVisibleResources() {
        removeCallbacks(frame);framePending=false
        removeCallbacks(wakeFrame);wakePosted.set(false);removeCallbacks(staleFrame)
        removeCallbacks(playbackFrame);playbackFramePending=false
        owner?.let{
            HandsFreePanelSignal.setListener(it,null);HandsFreePanelSignal.detach(it)
            HandsFreePlaybackSpectrum.setListener(it,null);HandsFreePlaybackSpectrum.detach(it)
        }
        signalBound=false;amplitudes.fill(0f);playbackDisplay.clear()
        if(scaleObserved){context.contentResolver.unregisterContentObserver(scaleChanged);scaleObserved=false}
    }
    private fun updateMotion() {
        val scale=Settings.Global.getFloat(context.contentResolver,Settings.Global.ANIMATOR_DURATION_SCALE,Float.NaN)
        val platformEnabled=if(Build.VERSION.SDK_INT>=26)ValueAnimator.areAnimatorsEnabled() else true
        // Battery saver is not the owner's explicit "Remove animations" choice.
        // Do not turn useful live audio feedback into 200ms jumps because of it.
        val animations=handsFreePanelMotionAllowed(scale,platformEnabled)
        reduceMotion=!animations
        playbackDisplay.clear()
        removeCallbacks(frame);framePending=false;removeCallbacks(staleFrame)
        removeCallbacks(playbackFrame);playbackFramePending=false
        if(canRender()) {
            if(reduceMotion && inputState())postDelayed(staleFrame,HandsFreePanelSignal.STALE_CLEAR_MS)
            invalidate()
        }
    }
    override fun onAttachedToWindow(){super.onAttachedToWindow();resumeVisibleResources()}
    override fun onDetachedFromWindow(){hide();super.onDetachedFromWindow()}
    override fun onWindowVisibilityChanged(visibility:Int){super.onWindowVisibilityChanged(visibility);if(!lifecycleReady)return;if(visibility!=VISIBLE)pauseVisibleResources() else resumeVisibleResources()}
    override fun onVisibilityChanged(changedView:View,visibility:Int){super.onVisibilityChanged(changedView,visibility);if(!lifecycleReady)return;if(!isShown)pauseVisibleResources() else resumeVisibleResources()}
    override fun onDraw(canvas:Canvas) {
        super.onDraw(canvas)
        if(!canRender())return
        val now=SystemClock.elapsedRealtime()
        val motion=!reduceMotion
        paint.color=0xff91a9ee.toInt();paint.style=Paint.Style.FILL;paint.alpha=255
        val centerX=width/2f;val centerY=height/2f
        when(state) {
            HandsFreePanelState.CAPTURING,HandsFreePanelState.CONTINUATION->{
                val active=HandsFreePanelSignal.render(owner,now,amplitudes)
                val left=width*.06f;val right=width*.94f
                val slot=(right-left)/amplitudes.size
                val bar=minOf(3.2f*density,slot*.46f)
                var hasVoice=false
                for(index in amplitudes.indices) {
                    val amplitude=amplitudes[index]
                    if(amplitude<=.002f)continue
                    hasVoice=true
                    val half=.65f*density+amplitude*(height*.40f)
                    val x=left+(index+.5f)*slot
                    paint.alpha=(36+219*amplitude).toInt().coerceIn(0,255)
                    bounds.set(x-bar/2,centerY-half,x+bar/2,centerY+half)
                    canvas.drawRoundRect(bounds,bar/2,bar/2,paint)
                }
                if(!hasVoice) {
                    paint.alpha=24;paint.strokeWidth=density
                    canvas.drawLine(left,centerY,right,centerY,paint)
                }
                if(motion && active)scheduleFrame()
            }
            HandsFreePanelState.PROCESSING->{
                // Match the desktop's start of cycle and keep the first frame for reduced motion.
                val elapsed=if(motion)(now-thoughtStartedAtMs).coerceAtLeast(0L) else 0L
                HandsFreeThinkingMotion.sample(elapsed,thoughtPositions)
                for(index in 0..2) {
                    val offset=index*2
                    canvas.drawCircle(centerX+thoughtPositions[offset]*1.75f*density,
                        centerY+thoughtPositions[offset+1]*1.75f*density,3.5f*density,paint)
                }
                if(motion)scheduleFrame()
            }
            HandsFreePanelState.SPEAKING->{
                val active=playbackDisplay.render(owner,motion)
                val playbackBands=playbackDisplay.levels
                val left=width*.06f;val right=width*.94f
                val slot=(right-left)/playbackBands.size
                val bar=minOf(5.2f*density,slot*.46f)
                val base=height*.89f
                var hasVoice=false
                for(index in playbackBands.indices) {
                    val amplitude=playbackBands[index]
                    if(amplitude<=.002f)continue
                    hasVoice=true
                    val x=left+(index+.5f)*slot
                    paint.alpha=(102+153*amplitude).toInt().coerceIn(0,255)
                    bounds.set(x-bar/2,base-.65f*density-amplitude*(height*.54f),x+bar/2,base)
                    canvas.drawRoundRect(bounds,bar/2,bar/2,paint)
                }
                if(!hasVoice) {
                    paint.alpha=24;paint.strokeWidth=density
                    canvas.drawLine(left,base,right,base,paint)
                }
                // Real output levels are still updated with reduced motion.
                if(active)schedulePlaybackFrame()
            }
            HandsFreePanelState.ERROR->{
                paint.color=0xfff2b5b5.toInt();paint.textSize=27f*density;paint.textAlign=Paint.Align.CENTER
                canvas.drawText("!",centerX,centerY+9f*density,paint)
            }
            else->Unit
        }
        paint.alpha=255
    }
}
