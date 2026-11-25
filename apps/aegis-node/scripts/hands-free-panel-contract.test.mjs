import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const android = name => readFileSync(new URL(`../src-tauri/gen/android/app/src/main/java/com/aegis/node/${name}.kt`, import.meta.url), 'utf8');

test('the assistant stays a bottom, transparent, card-touch-only window with scoped screen retention', () => {
  const panel = android('NodeVoiceInteractionSession');
  assert.match(panel, /:VoiceInteractionSession\(context\)/);
  assert.match(panel, /setGravity\(Gravity\.BOTTOM/);
  assert.match(panel, /setLayout\([^\n]*WindowManager\.LayoutParams\.WRAP_CONTENT\)/);
  assert.match(panel, /setBackgroundDrawableResource\(android\.R\.color\.transparent\)/);
  assert.match(panel, /clearFlags\([^\n]*FLAG_KEEP_SCREEN_ON[^\n]*FLAG_DIM_BEHIND/);
  const policy = panel.slice(panel.indexOf('private fun updateScreenPolicy('), panel.indexOf('private fun releaseWindowFlags('));
  assert.match(policy, /handsFreePanelKeepsScreenOn\(presentation\.state,visible,boundSession!=null\)/);
  assert.match(policy, /if\(keep\)it\.addFlags\(WindowManager\.LayoutParams\.FLAG_KEEP_SCREEN_ON\)/);
  assert.match(policy, /else it\.clearFlags\(WindowManager\.LayoutParams\.FLAG_KEEP_SCREEN_ON\)/);
  assert.match(panel, /outInsets\.touchableInsets=Insets\.TOUCHABLE_INSETS_REGION/);
  assert.match(panel, /safe\.bottom/);
});

test('hiding presentation releases drawing callbacks and leaves runtime capture and playback alive', () => {
  const panel = android('NodeVoiceInteractionSession');
  for (const method of ['onHide', 'onDestroy']) {
    const body = panel.match(new RegExp(`override fun ${method}\\(\\)\\{([^\\n]+)\\}`))?.[1];
    assert.ok(body);
    assert.match(body, /stopVisuals\(\)/);
    assert.doesNotMatch(body, /cancelSession|configure|\.stop\(|\.cancel\(/);
  }
  assert.match(panel, /private fun stopVisuals\(\)\{[^\n]*HandsFreePanelSignal\.detach\(this\)[^\n]*visualizer\?\.hide\(\)/);
  assert.match(panel, /shown=false\s*\n\s*pauseVisibleResources\(\)/);
  assert.match(panel, /removeCallbacks\(frame\);framePending=false/);
  assert.match(panel, /removeCallbacks\(wakeFrame\);wakePosted\.set\(false\);removeCallbacks\(staleFrame\)/);
  assert.match(panel, /HandsFreePanelSignal\.setListener\(it,null\);HandsFreePanelSignal\.detach\(it\)/);
  assert.match(panel, /private fun stopVisuals\(\)\{presentation\.end\(\);removeOutsideDismissal\(\)/);
});

test('display measurement reuses captured PCM without another audio source, network, or WebView events', () => {
  const renderer = android('NodeHandsFreePanel') + android('NodeVoiceInteractionSession');
  assert.doesNotMatch(renderer, /AudioRecord|MediaRecorder|OkHttp|HttpURLConnection|evaluateJavascript|loadUrl|WakeLock|AccessibilityService/);
  const runtime = android('NodeHandsFree');
  assert.match(runtime, /if\(captured \|\| continuation\)HandsFreePanelSignal\.capture\(sessionId,buffer,count,now\)/);
  assert.match(renderer, /fun capture\(session:String\?,samples:ShortArray,count:Int,nowMs:Long\)/);
  assert.match(renderer, /ValueAnimator\.areAnimatorsEnabled\(\)/);
  assert.doesNotMatch(renderer, /accessibility\.isEnabled|AccessibilityManager/);
  assert.match(renderer, /Settings\.Global\.getUriFor\(Settings\.Global\.ANIMATOR_DURATION_SCALE\)/);
  assert.doesNotMatch(renderer, /isPowerSaveMode|ACTION_POWER_SAVE_MODE_CHANGED/);
  assert.match(renderer, /unregisterContentObserver\(scaleChanged\)/);
});

test('screen-off only cancels the one-shot wake and closing always releases display retention', () => {
  const panel = android('NodeVoiceInteractionSession');
  const suppress = panel.slice(panel.indexOf('internal fun suppressScreenOn('), panel.indexOf('private fun updateScreenPolicy('));
  assert.match(suppress, /clearFlags\(WindowManager\.LayoutParams\.FLAG_TURN_SCREEN_ON\)/);
  assert.doesNotMatch(suppress, /addFlags|FLAG_KEEP_SCREEN_ON/);
  for (const method of ['onHide', 'onDestroy']) {
    const body = panel.match(new RegExp(`override fun ${method}\\(\\)\\{([^\\n]+)\\}`))?.[1];
    assert.match(body, /visible=false;stopVisuals\(\);releaseWindowFlags\(\)/);
  }
});

test('waveform uses measured temporal amplitudes and a single vsync callback without fixed position weights', () => {
  const panel = android('NodeVoiceInteractionSession');
  const draw = panel.slice(panel.indexOf('override fun onDraw('));
  assert.match(panel, /FloatArray\(HandsFreePanelSignal\.BAR_COUNT\)/);
  assert.match(draw, /HandsFreePanelSignal\.render\(owner,now,amplitudes,smooth=motion\)/);
  assert.match(draw, /val amplitude=amplitudes\[index\]/);
  assert.match(draw, /val bar=handsFreeInputBarWidth\(slot,density\)/);
  assert.match(draw, /val half=handsFreeInputBarHeight\(amplitude,density,height\.toFloat\(\)\)\/2f/);
  assert.match(draw, /val x=left\+\(index\+\.5f\)\*slot/);
  const signal = android('NodeHandsFreePanel');
  assert.match(signal, /const val BAR_COUNT=32/);
  assert.match(signal, /const val BAR_STEP_MS=60L/);
  assert.match(signal, /const val PRESENTATION_DELAY_MS=200L/);
  assert.match(signal, /val at=snapshotHead-\(BAR_COUNT-1-index\)\*slotNs/);
  assert.match(signal, /val blend=if\(smooth\)fraction else 0f/);
  assert.match(signal, /val visual=from\+\(sqrt\(right\)-from\)\*blend/);
  assert.match(signal, /minOf\(3f\*density,slot\*\.75f\)/);
  assert.match(signal, /\(3f\+sqrt\(normalized\)\*29f\)\*density/);
  assert.doesNotMatch(draw, /index-4|val weight=|for\(index in 0\.\.8\)|FloatArray\(|RectF\(/);
  assert.match(panel, /if\(canRender\(\) && !framePending\)/);
  assert.match(panel, /postOnAnimation\(frame\)/);
  assert.doesNotMatch(panel, /postDelayed\(frame,/);
  assert.match(panel, /if\(motion && active\)scheduleFrame\(\)/);
});

test('reduced motion redraws measured batches and expires stalled history without a permanent timer', () => {
  const panel = android('NodeVoiceInteractionSession');
  assert.match(panel, /private val wakeFrame=Runnable\{[\s\S]*?if\(reduceMotion\) \{/);
  assert.match(panel, /postDelayed\(staleFrame,HandsFreePanelSignal\.STALE_CLEAR_MS\)/);
  assert.match(panel, /private val staleFrame=Runnable\{if\(canRender\(\) && inputState\(\)\)/);
  assert.match(panel, /private fun inputState\(\)=state==HandsFreePanelState\.CAPTURING \|\| state==HandsFreePanelState\.CONTINUATION/);
  assert.match(panel, /override fun onWindowVisibilityChanged[^\n]*pauseVisibleResources\(\)/);
});

test('processing uses an animated cluster and playback the measured spectrum with scoped scheduling', () => {
  const panel = android('NodeVoiceInteractionSession');
  const processing = panel.slice(panel.indexOf('HandsFreePanelState.PROCESSING->{'), panel.indexOf('HandsFreePanelState.SPEAKING->{'));
  const playback = panel.slice(panel.indexOf('HandsFreePanelState.SPEAKING->{'), panel.indexOf('HandsFreePanelState.ERROR->{'));
  assert.match(processing, /canvas\.drawCircle/);
  assert.match(panel, /private val thoughtPositions=FloatArray\(6\)/);
  assert.match(panel, /if\(next==HandsFreePanelState\.PROCESSING\)thoughtStartedAtMs=now/);
  assert.match(processing, /val elapsed=if\(motion\)\(now-thoughtStartedAtMs\)\.coerceAtLeast\(0L\) else 0L/);
  assert.match(processing, /HandsFreeThinkingMotion\.sample\(elapsed,thoughtPositions\)/);
  assert.match(processing, /thoughtPositions\[offset\]\*1\.75f\*density/);
  assert.match(processing, /3\.5f\*density,paint/);
  assert.doesNotMatch(processing, /FloatArray\(|now%1800/);
  assert.match(processing, /if\(motion\)scheduleFrame\(\)/);
  assert.match(panel, /private val playbackDisplay=HandsFreePlaybackSpectrum\.Display\(\)/);
  assert.match(playback, /playbackDisplay\.render\(owner,motion\)/);
  assert.match(playback, /val playbackBands=playbackDisplay\.levels/);
  assert.match(playback, /amplitude\*\(height\*\.54f\)/);
  assert.match(panel, /playbackDisplay\.clear\(\)/);
  assert.match(playback, /canvas\.drawRoundRect/);
  assert.match(playback, /if\(active\)schedulePlaybackFrame\(\)/);
  assert.doesNotMatch(playback, /canvas\.drawArc|kotlin\.math\.sin|HandsFreePanelSignal\.render/);
  assert.match(panel, /postDelayed\(playbackFrame,40L\)/);
  assert.match(panel, /removeCallbacks\(playbackFrame\);playbackFramePending=false/);
  assert.match(panel, /HandsFreePlaybackSpectrum\.attach\(it,session\)/);
  assert.match(panel, /HandsFreePlaybackSpectrum\.setListener\(it,null\);HandsFreePlaybackSpectrum\.detach\(it\)/);
  assert.match(panel, /else->Unit\s*\n\s*\}\s*\n\s*paint.alpha=255/);
});

test('interruption availability uses accessibility rather than a separate visible instruction', () => {
  const panel = android('NodeVoiceInteractionSession');
  assert.doesNotMatch(panel, /bargeInHint: TextView|renderBargeInHint/);
  assert.match(panel, /updatePlaybackAccessibility\(presentation\.bargeInAccessibilityHint\(token, availability\)\)/);
  assert.match(panel, /visualizer\?\.contentDescription=if\(active\)/);
  assert.match(panel, /visualizer\?\.importantForAccessibility=if\(active\)View\.IMPORTANT_FOR_ACCESSIBILITY_YES/);
  assert.match(panel, /updateErrorLabel\(\);visualizer\?\.state\(presentation\.state\)/);
  assert.match(panel, /label\?\.text=if\(error\)presentation\.state\.label else ""/);
  assert.match(panel, /label\?\.visibility=if\(error\)View\.VISIBLE else View\.GONE/);
  const layout = panel.slice(panel.indexOf('override fun onCreateContentView('), panel.indexOf('override fun onShow('));
  assert.match(layout, /text="Aegis"[^\n]*gravity=Gravity\.CENTER/);
  assert.doesNotMatch(layout, /text=presentation\.state\.label|Button\(|text="×"|setMargins\(dp\(52\)/);
  assert.match(layout, /panel\.addView\(view,LinearLayout\.LayoutParams\(LinearLayout\.LayoutParams\.MATCH_PARENT,dp\(72\)\)\)/);
  assert.match(layout, /installAccessibleDismissal\(view\)/);
  assert.match(panel, /state in setOf\("speaking","processing"\)/);
  assert.match(android('NodeHandsFreePanel'), /HandsFreePanelState\.PROCESSING->if\(availability==HandsFreeBargeInPanelAvailability\.VOICE\)/);
});

test('outside touch, focused Back and accessible dismissal cancel only the still-bound interaction', () => {
  const panel = android('NodeVoiceInteractionSession');
  const show = panel.slice(panel.indexOf('override fun onShow('), panel.indexOf('internal fun installOutsideDismissal('));
  assert.match(show, /FLAG_NOT_TOUCH_MODAL/);
  assert.match(show, /installOutsideDismissal\(this\)/);
  const outside = panel.slice(panel.indexOf('internal fun installOutsideDismissal('), panel.indexOf('private fun cancelPresented('));
  assert.match(outside, /val snapshot=presentation\.snapshot\(\)\?:return/);
  assert.match(outside, /object:Window\.Callback by previous/);
  assert.match(outside, /event\.actionMasked==MotionEvent\.ACTION_OUTSIDE/);
  assert.match(outside, /cancelPresented\(snapshot\)\s*\n\s*return true/);
  assert.match(outside, /return previous\.dispatchTouchEvent\(event\)/);
  assert.match(outside, /target\.addFlags\(WindowManager\.LayoutParams\.FLAG_WATCH_OUTSIDE_TOUCH\)/);
  assert.match(outside, /installed\.window\.callback===installed\.wrapper\)installed\.window\.callback=installed\.previous/);
  assert.match(outside, /if\(!installed\.alreadyWatching\)installed\.window\.clearFlags\(WindowManager\.LayoutParams\.FLAG_WATCH_OUTSIDE_TOUCH\)/);
  const cancel = panel.slice(panel.indexOf('private fun cancelPresented('), panel.indexOf('override fun onComputeInsets('));
  assert.match(cancel, /!visible \|\| current!==this \|\| !presentation\.isCurrent\(snapshot\) \|\| boundSession!=snapshot\.session/);
  assert.match(cancel, /snapshot\.session\?\.let\(cancelInteraction\)\} finally \{finishQuietly\(\)\}/);
  assert.match(panel, /constructor\(context:Context\):this\(context,\{token->NodeHandsFree\.runtime\(context\)\.cancelSession\(token\)\}\)/);
  assert.match(cancel, /override fun onBackPressed\(\) \{\s*if\(!cancelPresented\(presentation\.snapshot\(\)\)\)super\.onBackPressed\(\)/);
  assert.match(cancel, /AccessibilityAction\(AccessibilityNodeInfo\.ACTION_DISMISS,"Cancelar interação"\)/);
  assert.match(cancel, /action==AccessibilityNodeInfo\.ACTION_DISMISS && \(host===visualizer \|\| host===label\)/);
});

test('processing interruption has exclusive microphone ownership before playback and cancels only the old turn', () => {
  const runtime = android('NodeHandsFree');
  const processing = runtime.slice(runtime.indexOf('private fun startProcessingCapture('), runtime.indexOf('private fun retireProcessingCapture('));
  const retire = runtime.slice(runtime.indexOf('private fun retireProcessingCapture('), runtime.indexOf('private fun startResponseCapture('));
  assert.match(processing, /AegisProcessingMic/);
  assert.match(processing, /listener\.startAsync\(\)/);
  assert.match(processing, /!response\.processing \|\| response\.playing \|\| responseCapture!==listener/);
  assert.match(processing, /!ownsTurn\(generation,session,owner\)/);
  assert.match(processing, /activation\.identity!=currentIdentity\(\)/);
  assert.match(processing, /backend=null/);
  assert.match(processing, /owner\.requestCancellation\(\)/);
  assert.ok(processing.indexOf('backend=null') < processing.indexOf('owner.requestCancellation()'));
  assert.match(processing, /owner\.playbackInterruption\(0\)/);
  assert.match(processing, /owner\.generationInterruption\(\)/);
  assert.doesNotMatch(processing, /cancelSession\(|\bbeep\(/);
  assert.match(retire, /response\.processing=false/);
  assert.match(retire, /listener\.close\(\)/);
  assert.ok(retire.indexOf('response.processing=false') < retire.indexOf('listener.close()'));
  assert.match(retire, /listener\.awaitClosed\(1500\)/);
  const prepare = runtime.slice(runtime.indexOf('prepareCommunication = {'), runtime.indexOf('onCommunicationFailed = {'));
  assert.ok(prepare.includes('retireProcessingCapture('));
  assert.ok(prepare.indexOf('retireProcessingCapture(') < prepare.indexOf('HandsFreeConversationAudio.tryOpen('));
  assert.match(runtime, /interruptedGeneration=pendingGenerationInterruption\?\.second/);
  assert.match(runtime, /onProcessing=\{if\(response!=null\)startProcessingCapture\(/);
});

test('temporary transcription still rejects updates from a previous session or request', () => {
  const panel = android('NodeVoiceInteractionSession');
  const partial = panel.slice(panel.indexOf('internal fun showPartial('), panel.indexOf('internal fun suppressScreenOn'));
  assert.match(partial, /boundSession!=token \|\| !visible/);
  assert.match(partial, /if\(partialRequestId!=requestId\)return/);
  assert.match(partial, /HandsFreePartialDisplayLimiter\.MAX_CHARACTERS/);
});

test('terminal failure presentation is explicit and scoped to the still-bound session', () => {
  const panel = android('NodeVoiceInteractionSession');
  const close = panel.slice(panel.indexOf('internal fun finishIfSession('), panel.indexOf('internal fun finishQuietly('));
  assert.ok(close.indexOf('if(boundSession!=token)return') < close.indexOf('presentation.delayClose(immediate,failed)'));
  const runtime = android('NodeHandsFree');
  assert.match(runtime, /finishIfSession\(token,immediate,failed\)/);
  assert.match(runtime, /closePanel\(session,failed=!cancelled\)/);
});

test('generic cancellation of a terminal error cannot dismiss a newly activated or rebound panel', () => {
  const panel = android('NodeVoiceInteractionSession');
  const runtime = android('NodeHandsFree');
  const cancellation = runtime.slice(runtime.indexOf('fun cancelSession('), runtime.indexOf('internal fun activateFromAssistant('));
  assert.match(cancellation, /expectedSession==null\)NodeVoiceInteractionSession\.current\?\.cancellationSnapshot\(\)/);
  assert.match(cancellation, /if\(expectedSession!=null && expectedSession!=sessionId\)return/);
  assert.match(cancellation, /main\.post\{synchronized\(lock\)\{if\(sessionId==null && !pendingActivation\)panelCancellation\.run\(\)\}\}/);
  assert.match(panel, /Runnable\{if\(current===this && presentation\.isCurrent\(snapshot\)\)finishQuietly\(\)\}/);
  assert.match(panel, /private fun stopVisuals\(\)\{presentation\.end\(\)/);
});

test('Windows voice transitions update the current app and its tray icon without activating a window', () => {
  const rust = name => readFileSync(new URL(`../src-tauri/src/${name}.rs`, import.meta.url), 'utf8');
  const worker = rust('windows_hands_free');
  const publish = worker.slice(worker.indexOf('fn publish('), worker.indexOf('fn view('));
  const shell = rust('native_notifications');
  const tooltip = shell.slice(shell.indexOf('pub fn set_voice_tooltip('), shell.indexOf('pub fn tray('));
  assert.match(publish, /emit\(\s*"aegis-windows-voice-state"/);
  assert.match(publish, /native_notifications::set_voice_tooltip\(/);
  assert.match(shell, /TrayIconBuilder::with_id\(TRAY_ID\)/);
  assert.match(tooltip, /tray_by_id\(TRAY_ID\)/);
  assert.match(tooltip, /tray\.set_tooltip\(/);
  assert.doesNotMatch(publish + tooltip,
    /windows_voice_panel|CreateWindowEx|ShowWindow|WS_EX_TOPMOST|WebviewWindowBuilder|\.set_focus\(|\.unminimize\(|\.show\(|::open\(/);
});
