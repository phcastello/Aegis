import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const source = readFileSync(new URL('../src-tauri/src/windows_hands_free.rs', import.meta.url), 'utf8');

test('only a granted Windows activation pauses media and plays the cue', () => {
  const cycle = source.slice(source.indexOf('fn cycle('), source.indexOf('const MEDIA_OPERATION_BUDGET'));
  const lookup = cycle.indexOf('let lookup = MediaManager::RequestAsync().ok()');
  const claim = cycle.indexOf('let grant = backend.claim(');
  const suppressed = cycle.indexOf('if !matches!(grant, Ok(true)) || cancel.is_cancelled()');
  const winner = cycle.indexOf('let granted_at =');
  const pause = cycle.indexOf('let _paused = PausedMedia::new(manager)');
  const cue = cycle.indexOf('windows_voice_audio::cue(cancel)?');
  const endpoint = cycle.indexOf('endpoint.after_cue(mic.pending())');
  assert.ok(lookup >= 0 && lookup < claim && claim < suppressed && suppressed < winner && winner < pause && pause < cue && cue < endpoint);
  assert.match(cycle.slice(suppressed, winner), /operation\.Cancel\(\)[\s\S]*?return if cancel\.is_cancelled\(\)/);
  assert.doesNotMatch(cycle.slice(0,winner), /GetCurrentSession|TryPauseAsync|PausedMedia::new|windows_voice_audio::cue/);
  const guardStart = source.indexOf('impl PausedMedia');
  const pauseGuard = source.slice(guardStart, source.indexOf('#[cfg(test)]', guardStart));
  assert.match(pauseGuard, /Playback::Playing[\s\S]*?TryPauseAsync\(\)[\s\S]*?Ok\(Ok\(true\)\) => Some\(session\)/);
  assert.match(pauseGuard, /if let Some\(session\) = &self\.0[\s\S]*?Playback::Paused[\s\S]*?TryPlayAsync\(\)/);
});
