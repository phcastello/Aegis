import assert from 'node:assert/strict';
import { test } from 'node:test';
import { pendingActionLabel, actionDiagnosticRows, actionDiagnosticsUnavailable, mergeActionSettings } from '../.test-build/services/reminderActions.js';

test('action queue distinguishes Portuguese singular and plural', () => {
  assert.equal(pendingActionLabel(1), '1 ação pendente de reconhecimento ou adiamento');
  assert.equal(pendingActionLabel(2), '2 ações pendentes de reconhecimento ou adiamento');
});
test('unavailable native diagnostics preserve the last read instead of accepting fallback zeros', () => {
  const previous = { granted: true, pushConfigured: true, autostart: false, pendingActions: 2, failedActions: 1, actionSyncState: 'pending', actionDiagnostics: [{ type: 'snooze', state: 'pending' }] };
  const unavailable = { granted: false, pushConfigured: false, autostart: false, pendingActions: 0, failedActions: 0, actionSyncState: 'unavailable', actionDiagnostics: [], actionDiagnosticsAvailable: false };
  const current = mergeActionSettings(previous, unavailable);
  assert.equal(actionDiagnosticsUnavailable(current), true);
  assert.equal(current.actionSyncState, 'unavailable');
  assert.equal(current.pendingActions, 2);
  assert.equal(current.failedActions, 1);
  assert.deepEqual(current.actionDiagnostics, previous.actionDiagnostics);
  assert.equal(current.granted, false, 'unrelated fresh settings still update');
  assert.equal(mergeActionSettings(current).pendingActions, 2, 'IPC rejection also preserves the last read');
  const empty = { ...unavailable, actionDiagnosticsAvailable: true, actionSyncState: 'idle' };
  assert.deepEqual(mergeActionSettings(current, empty), empty, 'a later successful zero clears the old snapshot');
  assert.equal(actionDiagnosticsUnavailable(empty), false);
});
test('first unavailable diagnostics remain unknown and older Nodes retain known zero semantics', () => {
  const unavailable = mergeActionSettings(null, { granted: true, pushConfigured: true, autostart: false, pendingActions: 0, failedActions: 0, actionSyncState: 'unavailable' });
  assert.equal(actionDiagnosticsUnavailable(unavailable), true);
  assert.equal(unavailable.pendingActions, undefined);
  assert.equal(unavailable.failedActions, undefined);
  assert.equal(unavailable.actionDiagnostics, undefined);
  const legacy = { granted: true, pushConfigured: true, autostart: false, pendingActions: 0, actionSyncState: 'idle' };
  assert.equal(actionDiagnosticsUnavailable(legacy), false);
  assert.deepEqual(mergeActionSettings(unavailable, legacy), legacy);
});
test('bounded diagnostic retains outcome and retry without exposing arbitrary data', () => {
  const sensitive = 'token-secret https://private/ identifier';
  const rows = actionDiagnosticRows({ actionDiagnostics: [
    { type: 'snooze', ageSeconds: 3600, attempts: 3, lastAttemptAt: '2026-10-19T12:00:00Z', lastResult: 'http_temporary', state: 'pending', retryScheduled: true },
    { type: 'acknowledge', ageSeconds: 60, attempts: 1, lastResult: 'rejected', state: 'permanent_failure', retryScheduled: false },
    { type: 'acknowledge', ageSeconds: NaN, attempts: -1, lastAttemptAt: sensitive, lastResult: sensitive, state: sensitive, retryScheduled: sensitive },
    { type: sensitive, ageSeconds: 60, attempts: 1, lastResult: sensitive, state: 'pending' },
  ] });
  assert.equal(rows.length, 3);
  assert.equal(rows[0].type, 'Adiamento'); assert.equal(rows[0].age, '1 h'); assert.equal(rows[0].retryScheduled, true);
  assert.equal(rows[1].state, 'Falha permanente'); assert.equal(rows[1].lastResult, 'Rejeitada definitivamente');
  assert.equal(rows[2].lastAttempt, 'Ainda não tentada');
  assert.doesNotMatch(JSON.stringify(rows), /token-secret|private|identifier/);
  assert.equal(actionDiagnosticRows({ actionDiagnostics: Array.from({ length: 100 }, () => ({ type: 'snooze', ageSeconds: 1, attempts: 0, state: 'pending' })) }).length, 30);
});
