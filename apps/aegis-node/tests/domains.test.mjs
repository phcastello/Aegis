import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { mockIPC, clearMocks } from '@tauri-apps/api/mocks';
import { remindersApi, placesApi, locationApi, geofencesApi, recurrenceLabel, geofenceLabels, localInput, domainError } from '../.test-build/services/domains.js';
afterEach(() => clearMocks());
test('domain adapters use native domain commands and keep credential retrieval outside the WebView', async () => {
  globalThis.window = {}; const calls = [];
  mockIPC((command, args) => { calls.push({command,args}); return {}; });
  await remindersApi('action', {actionId:'action',expectedRevision:2,generation:3,action:'snooze',snoozeMinutes:10},'observed-occurrence');
  await placesApi('remove',{expectedRevision:4},'observed-place');
  await locationApi('selectSource',{sourceNodeId:'observed-mobile'});
  await geofencesApi('sync');
  assert.deepEqual(calls.map(call=>call.command),['node_reminders','node_places','node_location','node_geofences']);
  assert.equal(calls[0].args.id,'observed-occurrence');assert.equal(calls[0].args.payload.expectedRevision,2);assert.equal(calls[0].args.payload.generation,3);
  assert.doesNotMatch(JSON.stringify(calls),/credential|Authorization|secret|https?:/);
});
test('recurrence summaries make intervals and calendar choices visible', () => {
  const base={interval:1,localTime:'08:00',startDate:'2026-01-01'};
  assert.equal(recurrenceLabel({...base,frequency:'daily'}),'Diariamente às 08:00');
  assert.match(recurrenceLabel({...base,frequency:'weekly',interval:2,weekdays:[1,3,5]}),/A cada 2 semanas, seg, qua, sex/);
  assert.match(recurrenceLabel({...base,frequency:'monthly',lastDay:true,count:5}),/último dia.*5 ocorrências/);
  assert.match(recurrenceLabel({...base,frequency:'monthly',monthDay:31,untilDate:'2027-01-01'}),/dia 31.*01\/01\/2027/);
  assert.equal(geofenceLabels.pending_sync,'Pendente de sincronização');assert.equal(geofenceLabels.waiting_initial,'Aguardando condição inicial');
  assert.equal(localInput('invalid'),'');
});

test('native validation messages stay useful without rendering malformed error objects or stacks', () => {
 assert.equal(domainError('O lugar mudou. Atualize a tela.','Falha'),'O lugar mudou. Atualize a tela.');
 assert.equal(domainError({credential:'private'},'Falha'),'Falha');assert.equal(domainError('Trace\n at internal','Falha'),'Falha');assert.equal(domainError('x'.repeat(301),'Falha'),'Falha');
});
