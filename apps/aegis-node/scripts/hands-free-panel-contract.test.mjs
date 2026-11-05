import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const android = name => readFileSync(new URL(`../src-tauri/gen/android/app/src/main/java/com/aegis/node/${name}.kt`, import.meta.url), 'utf8');

test('the assistant stays a bottom, transparent, card-touch-only system window without holding the display on', () => {
  const panel = android('NodeVoiceInteractionSession');
  assert.match(panel, /:VoiceInteractionSession\(context\)/);
  assert.match(panel, /setGravity\(Gravity\.BOTTOM/);
  assert.match(panel, /setLayout\([^\n]*WindowManager\.LayoutParams\.WRAP_CONTENT\)/);
  assert.match(panel, /setBackgroundDrawableResource\(android\.R\.color\.transparent\)/);
  assert.match(panel, /clearFlags\([^\n]*FLAG_KEEP_SCREEN_ON[^\n]*FLAG_DIM_BEHIND/);
  assert.doesNotMatch(panel, /addFlags\([^\n]*FLAG_KEEP_SCREEN_ON/);
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
  assert.match(panel, /setOnClickListener\{[^\n]*cancelSession\(it\)[^\n]*finishQuietly\(\)/);
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
  assert.match(renderer, /PowerManager\.ACTION_POWER_SAVE_MODE_CHANGED/);
  assert.match(renderer, /unregisterContentObserver\(scaleChanged\)/);
  assert.match(renderer, /unregisterReceiver\(powerChanged\)/);
});

test('waveform uses measured temporal amplitudes and a single vsync callback without fixed position weights', () => {
  const panel = android('NodeVoiceInteractionSession');
  const draw = panel.slice(panel.indexOf('override fun onDraw('));
  assert.match(panel, /FloatArray\(HandsFreePanelSignal\.BAR_COUNT\)/);
  assert.match(draw, /HandsFreePanelSignal\.render\(owner,now,amplitudes\)/);
  assert.match(draw, /val amplitude=amplitudes\[index\]/);
  assert.match(draw, /val half=\.65f\*density\+amplitude\*\(height\*\.40f\)/);
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

test('processing and playback use distinct state animations and idle does not schedule frames', () => {
  const panel = android('NodeVoiceInteractionSession');
  const processing = panel.slice(panel.indexOf('HandsFreePanelState.PROCESSING->{'), panel.indexOf('HandsFreePanelState.SPEAKING->{'));
  const playback = panel.slice(panel.indexOf('HandsFreePanelState.SPEAKING->{'), panel.indexOf('HandsFreePanelState.ERROR->{'));
  assert.match(processing, /canvas\.drawCircle/);
  assert.match(playback, /canvas\.drawArc/);
  assert.doesNotMatch(processing + playback, /HandsFreePanelSignal\.render|MicroWakeWordNative|HandsFreePlayback/);
  assert.match(panel, /else->Unit\s*\n\s*\}\s*\n\s*paint.alpha=255/);
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
