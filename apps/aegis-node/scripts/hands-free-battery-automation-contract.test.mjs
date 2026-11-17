import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const source = path => readFileSync(new URL(`../${path}`, import.meta.url), 'utf8');
const android = name => source(`src-tauri/gen/android/app/src/main/java/com/aegis/node/${name}.kt`);
const section = (text, begin, end) => {
  const start = text.indexOf(begin), stop = text.indexOf(end, start + begin.length);
  assert.ok(start >= 0 && stop > start, `Missing source boundary: ${begin}`);
  return text.slice(start, stop);
};

test('automatic comparison has native ownership while the settings timer only refreshes visible state', () => {
  const controls = source('src/composables/useHandsFreeSettings.ts');
  assert.doesNotMatch(controls, /setTimeout\s*\(|setInterval\s*\(|requestAnimationFrame\s*\(/);
  const start = section(controls, 'async function startAuto(', 'async function cancelAuto(');
  assert.match(start, /await request\(validation \? 'batteryAutoValidate' : 'batteryAutoStart'\)/);
  assert.doesNotMatch(start, /batteryStart|startBattery|30\s*\*\s*60|1800000/);
  const panel = source('src/components/HandsFreeSettings.vue').split('</script>')[0];
  const timers = [...panel.matchAll(/setInterval\(\(\) => \{([\s\S]*?)\},\s*\d+\)/g)];
  assert.ok(timers.length > 0, 'Visible state refresh remains available without owning the experiment');
  for (const [, body] of timers) {
    assert.match(body, /document\.visibilityState === 'visible'/);
    assert.match(body, /refresh\(\)/);
    assert.doesNotMatch(body, /startAuto|startBattery|cancelAuto|finishBattery|batteryAutoStart|batteryStart/);
  }
  const runtime = android('NodeHandsFree');
  assert.match(runtime, /HandsFreeBatteryAutomation\(context\)/);
  assert.match(runtime, /HandsFreeBatteryAlarm\(context\)/);
  assert.match(runtime, /batteryAutomation\.isWaiting\(\)/);
  assert.match(runtime, /batteryAutomation\.claimDue\(token\)/);
});

test('closed automatic operations keep matching Rust Activity and R8 JNI names without a timer payload', () => {
  const bridge = source('src-tauri/src/hands_free.rs');
  const signature = bridge.match(/pub async fn node_hands_free\(([\s\S]*?)\)\s*->\s*Result/)[1];
  assert.doesNotMatch(signature, /duration|deadline|sequence|stage|alarm|validation/);
  const operations = section(bridge, 'fn battery_auto_control(', '#[tauri::command]');
  assert.match(operations, /"batteryAutoStart" => Ok\(\("handsFreeBatteryAutoStart", json!\(\{"validation":false\}\)\)\)/);
  assert.match(operations, /"batteryAutoValidate" => Ok\(\("handsFreeBatteryAutoStart", json!\(\{"validation":true\}\)\)\)/);
  assert.match(operations, /"batteryAutoCancel" => Ok\(\("handsFreeBatteryAutoCancel", json!\(\{\}\)\)\)/);
  assert.match(operations, /_ => Err\(HandsFreeError::new\("invalid_request"\)\)/);
  const activity = android('MainActivity');
  const keep = source('src-tauri/gen/android/app/proguard-rules.pro');
  for (const [method, target, payload] of [
    ['handsFreeBatteryAutoStart', 'batteryAutoStart', ',payload'],
    ['handsFreeBatteryAutoCancel', 'batteryAutoCancel', '']
  ]) {
    assert.ok(activity.includes(`fun ${method}(payload:String):String=NodeHandsFree.${target}(this${payload})`));
    assert.ok(keep.includes(`public java.lang.String ${method}(java.lang.String);`));
  }
});

test('the alarm receiver is private and uses a bounded transition lease without opening audio itself', () => {
  const manifest = source('src-tauri/gen/android/app/src/main/AndroidManifest.xml');
  const receiver = manifest.match(/<receiver\b[^>]*android:name="\.NodeHandsFreeBatteryAlarmReceiver"[^>]*\/?>/)[0];
  assert.match(receiver, /android:exported="false"/);
  assert.match(receiver, /\/\s*>$/);
  assert.doesNotMatch(manifest, /android\.permission\.(?:SCHEDULE_EXACT_ALARM|USE_EXACT_ALARM|REQUEST_IGNORE_BATTERY_OPTIMIZATIONS)/);
  const alarm = android('NodeHandsFreeBatteryAlarm');
  const delivery = alarm.slice(alarm.indexOf('class NodeHandsFreeBatteryAlarmReceiver:'));
  assert.ok(delivery.indexOf('HandsFreeBatteryAlarmIntents.parse(intent) ?: return') < delivery.indexOf('goAsync()'));
  assert.match(delivery, /acquire\(TRANSITION_TIMEOUT_MS\)/);
  assert.match(delivery, /postDelayed\(completion, TRANSITION_TIMEOUT_MS\)/);
  assert.match(delivery, /TRANSITION_TIMEOUT_MS = 10000L/);
  assert.match(delivery, /NodeHandsFree\.batteryAlarm\(context\.applicationContext, token\)/);
  assert.doesNotMatch(delivery, /AudioRecord|startRecording|ensureForeground|startForeground|MicroWakeWordNative|logcat|Log\./);
  assert.match(alarm, /setExactAndAllowWhileIdle\(AlarmManager\.ELAPSED_REALTIME_WAKEUP/);
  assert.match(alarm, /setAndAllowWhileIdle\(AlarmManager\.ELAPSED_REALTIME_WAKEUP/);
  assert.match(alarm, /PendingIntent\.FLAG_IMMUTABLE/);
  assert.doesNotMatch(alarm, /setAlarmClock\(|setRepeating\(|setInexactRepeating\(/);
});

test('native transitions drain the old worker and schedule the next boundary before enabling capture', () => {
  const runtime = android('NodeHandsFree');
  const transition = section(runtime, 'private fun advanceBatteryAutomation(', 'internal fun observeBattery(');
  const stop = transition.indexOf('stop("battery_transition"');
  const joined = transition.indexOf('previous.join(1500)', stop);
  const started = transition.indexOf('batteryDiagnostics.start(', joined);
  const scheduled = transition.indexOf('batteryAlarm.schedule(next)', started);
  const enabled = transition.indexOf('batteryStopRequested=false', scheduled);
  assert.ok(stop >= 0 && joined > stop && started > joined && scheduled > started && enabled > scheduled);
  assert.match(transition, /check\(previous\?\.isAlive!=true\)/);
  assert.match(transition, /if\(step\.mode!="off"\)check\(service\?\.ensureForeground\(\)==true\)/);
  const capture = section(runtime, 'private fun wantsCapture()', 'private fun current(');
  assert.match(capture, /"off"->false/);
  const shutdown = section(runtime, 'private fun stop(', 'fun manualAcquire(');
  assert.match(shutdown, /stopHandsFreeForeground\(\)/);
  const start = section(runtime, 'internal fun startBatteryAutomation(', 'internal fun queueBatteryTransition(');
  assert.ok(start.indexOf('putBoolean("batteryStudySuspended",true).commit()') < start.indexOf('batteryAutomation.arm(validation)'));
  assert.match(start, /NodeHandsFree|batteryAutomationPrerequisites\(\)/);
});

test('delayed callbacks retain a generation guard and claim failures interrupt capture', () => {
  const runtime = android('NodeHandsFree');
  const queue = section(runtime, 'internal fun queueBatteryTransition(', 'private fun requestBatteryTransition(');
  assert.ok(queue.indexOf('val generation=batteryStudyGeneration.get()') < queue.indexOf('cleanup.execute'));
  assert.match(queue, /!batteryAbortPending && batteryStudyGeneration\.get\(\)==generation/);
  assert.match(queue, /catch\(_:[ ]*Throwable\)\{interruptBatteryStudy\("transition_failed"\)\}/);
  assert.match(queue, /finally\{onComplete\(\)\}/);
  const interrupt = section(runtime, 'internal fun interruptBatteryStudy(', 'private fun batteryIdentity(');
  assert.ok(interrupt.indexOf('batteryStopRequested=true') < interrupt.indexOf('cleanup.execute'));
  assert.ok(interrupt.indexOf('stop("battery_interrupted"') < interrupt.indexOf('cleanup.execute'));
  assert.match(interrupt, /batteryStudyGeneration\.incrementAndGet\(\)/);
  const transition = section(runtime, 'private fun advanceBatteryAutomation(', 'internal fun observeBattery(');
  assert.match(transition, /check\(!batteryAbortPending && batteryStudyGeneration\.get\(\)==generation\)/);
});
