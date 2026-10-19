import assert from 'node:assert/strict';
import { test } from 'node:test';
import { pendingActionLabel, actionDiagnosticRows } from '../.test-build/services/reminderActions.js';

test('action queue distinguishes Portuguese singular and plural', () => {
  assert.equal(pendingActionLabel(1), '1 ação pendente de reconhecimento ou adiamento');
  assert.equal(pendingActionLabel(2), '2 ações pendentes de reconhecimento ou adiamento');
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
