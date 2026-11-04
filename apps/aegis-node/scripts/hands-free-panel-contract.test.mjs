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
  assert.match(panel, /shown=false;level=0f;removeCallbacks\(frame\)/);
  assert.match(panel, /setOnClickListener\{[^\n]*cancelSession\(it\)[^\n]*finishQuietly\(\)/);
});

test('display measurement reuses captured PCM without another audio source, network, or WebView events', () => {
  const renderer = android('NodeHandsFreePanel') + android('NodeVoiceInteractionSession');
  assert.doesNotMatch(renderer, /AudioRecord|MediaRecorder|OkHttp|HttpURLConnection|evaluateJavascript|loadUrl|WakeLock|AccessibilityService/);
  const runtime = android('NodeHandsFree');
  assert.match(runtime, /if\(captured \|\| continuation\)HandsFreePanelSignal\.capture\(sessionId,buffer,count,now\)/);
  assert.match(renderer, /observation\?\.takeIf\{it\.session==session\}\?:return/);
  assert.match(renderer, /ValueAnimator\.areAnimatorsEnabled\(\)/);
  assert.match(renderer, /accessibility\.isEnabled/);
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
