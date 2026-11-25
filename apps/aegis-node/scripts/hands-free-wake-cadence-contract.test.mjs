import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = name => readFileSync(new URL(`../src-tauri/gen/android/app/src/main/java/com/aegis/node/${name}.kt`, import.meta.url), 'utf8');

test('Android accelerates only pending automatic wake candidates and retains every read before polling', () => {
  const runtime = source('NodeHandsFree');
  const capture = runtime.slice(runtime.indexOf('private fun capture('));
  assert.match(capture, /val electWake = !continuation && !batteryStudy && !diagnostic/);
  assert.match(capture, /val waitingForElection=electWake && !pendingActivation && \(wakeCandidate!=null \|\| pendingVerification!=null\)/);
  assert.match(capture, /handsFreePendingWakeReadSamples\(buffer.size,waitingForElection,waitingForElection && NodeExternalPlaybackReference.active\(\)\)/);
  assert.match(capture, /audio\.read\(buffer,0,readSize,AudioRecord\.READ_BLOCKING\)/);
  const append = capture.indexOf('pendingVerification?.append(buffer,count)');
  const poll = capture.indexOf('pending.poll(SystemClock.elapsedRealtime())');
  assert.ok(append >= 0 && append < poll, 'retain the complete microphone read before observing the decision');
  const captureOnly = runtime.slice(runtime.indexOf('private fun captureOnly('), runtime.indexOf('private fun capture('));
  assert.match(captureOnly, /audio\.read\(buffer,0,buffer\.size,AudioRecord\.READ_BLOCKING\)/);
});

test('Android cue remains gated by authenticated winner, command readiness and media focus', () => {
  const capture = source('NodeHandsFree').split('private fun capture(')[1];
  const activation = capture.indexOf('if(event>=0 || pendingActivation)');
  const authority = capture.indexOf('verifiedPending?.election?.canActivate()!=true', activation);
  const ready = capture.indexOf('commandCapture=HandsFreeCommandCapture(', authority);
  const focus = capture.indexOf('sessionMediaFocus.acquire(session)', ready);
  const cue = capture.indexOf('cueCompletion=beep()', focus);
  const stt = capture.indexOf('activeBackend.beginCapture(', cue);
  assert.ok(activation >= 0 && activation < authority && authority < ready && ready < focus && focus < cue && cue < stt);
  assert.match(capture.slice(authority, ready), /continue/);
  assert.match(capture.slice(focus, cue), /cancelSession\(session,"audio_focus_lost"\)/);
  assert.match(capture.slice(cue, stt), /wake_grant_observation_wait/);
  const worker = source('NodeHandsFreeCue');
  assert.ok(worker.indexOf('ready.complete()') < worker.indexOf('onStarted(it)'), 'telemetry cannot delay cue-completion gate');
});

test('Android credits elapsed verified-candidate age without changing claim JSON or introducing idle requests', () => {
  const election = source('NodeHandsFreeWakeElection');
  const claim = election.slice(election.indexOf('fun claim('), election.indexOf('fun grantedIdentity('));
  assert.match(claim, /remaining = deadlineMs - clock\(\)/);
  assert.match(claim, /header\("X-Aegis-Detection-Age-Ms", \(MAX_AGE_MS - remaining\)\.toString\(\)\)/);
  assert.doesNotMatch(claim, /payload\.put\(".*[Aa]ge/);
  assert.match(claim, /call\.timeout\(\)\.timeout\(remaining, TimeUnit\.MILLISECONDS\)/);
  assert.equal(claim.match(/call\.execute\(\)/g)?.length, 1);
  const pending = source('NodeHandsFreePendingWakeVerification');
  assert.match(pending, /if\(score>=threshold && !stopped\.get\(\)\)election\?\.claim/);
  const capture = source('NodeHandsFree').split('private fun capture(')[1];
  const cue = capture.indexOf('cueCompletion=beep()');
  const metrics = capture.indexOf('metrics.timing("wake_server_election_wait"', cue);
  const stt = capture.indexOf('activeBackend.beginCapture(', cue);
  assert.ok(cue < metrics && metrics < stt);
  assert.match(capture.slice(cue, stt), /coordinatorTiming\?\.let/);
  assert.match(capture.slice(cue, stt), /metrics\.timing\("wake_coordinator_transport_overhead",timing\.transportOverheadMs/);
  const backend = source('NodeHandsFreeBackend');
  assert.match(backend, /connectionPool\(ConnectionPool\(1,5,TimeUnit.MINUTES\)\)/);
  assert.doesNotMatch(backend, /\.pingInterval\(/);
  assert.match(backend, /followRedirects\(false\)\.followSslRedirects\(false\)\.retryOnConnectionFailure\(false\)/);
});
