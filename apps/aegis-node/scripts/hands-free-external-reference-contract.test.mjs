import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const read = path => readFileSync(new URL('../' + path, import.meta.url), 'utf8');
const android = name => read('src-tauri/gen/android/app/src/main/java/com/aegis/node/' + name + '.kt');

test('media reference has explicit projection consent and non-exported foreground service', () => {
  const manifest = read('src-tauri/gen/android/app/src/main/AndroidManifest.xml');
  assert.match(manifest, /android\.permission\.FOREGROUND_SERVICE_MEDIA_PROJECTION/);
  assert.match(manifest, /<service android:name="\.NodeExternalPlaybackReferenceService" android:exported="false" android:foregroundServiceType="mediaProjection"/);
  const plugin = android('NodeNotificationsPlugin');
  assert.match(plugin, /startActivityForResult\(invoke,manager\.createScreenCaptureIntent\(\),"handsFreeMediaReferenceCallback"\)/);
  assert.match(plugin, /finishConsent\(activity,id,result\.resultCode,result\.data\)/);
  const reference = android('NodeExternalPlaybackReference');
  assert.match(reference, /resultCode != Activity\.RESULT_OK \|\| data == null/);
  assert.match(reference, /ContextCompat\.startForegroundService/);
  const service = android('NodeExternalPlaybackReferenceService');
  const start = service.indexOf('startForeground(NOTIFICATION');
  assert.ok(start >= 0 && start < service.indexOf('.getMediaProjection'));
  assert.match(service, /override fun onStop\(\)/);
  assert.match(service, /START_NOT_STICKY/);
  assert.doesNotMatch(service + reference, /createVirtualDisplay\(|MediaRecorder\(|FileOutputStream|SharedPreferences|OkHttp|HttpURLConnection/);
});

test('only allowed external playback usage is captured; no listener means no recorder', () => {
  const service = android('NodeExternalPlaybackReferenceService');
  assert.deepEqual([...service.matchAll(/\.addMatchingUsage\(AudioAttributes\.(\w+)\)/g)].map(row => row[1]), ['USAGE_MEDIA', 'USAGE_GAME', 'USAGE_UNKNOWN']);
  assert.match(service, /\.excludeUid\(android\.os\.Process\.myUid\(\)\)/);
  assert.match(service, /\.setAudioPlaybackCaptureConfig\(configuration\)/);
  assert.match(service, /AudioTimestamp\.TIMEBASE_MONOTONIC/);
  assert.match(service, /if \(!failedEpisode\) startCollector\(\)/);
  assert.match(service, /if \(playing \|\| worker != null\) main\.postDelayed\(watchdog, 4000L\)/);
  assert.match(service, /record\?\.release\(\)/);
  assert.match(service, /releaseRecord\(ownedSession\)/);
  assert.match(service, /pcm\.fill\(0\)/);
});
