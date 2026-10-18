import assert from 'node:assert/strict';
import { test } from 'node:test';
import { currentGeofenceDiagnostic, geofenceMonitoringStatus, geofencePresenceReason, geofenceDiagnosticHistory } from '../.test-build/services/geofenceMonitoring.js';

const now = Date.parse('2026-10-18T12:00:00Z');
const rule = { id: 'rule', revision: 4, status: 'active', syncStatus: 'installed', transition: 'enter' };
const observed = { id: rule.id, revision: rule.revision, installationStatus: 'installed', presence: 'unknown', armed: false, lastObservationAt: null, accuracyMeters: null, reasonCode: 'not_observed', powerSaveMode: false };
const status = (changes = {}, item = rule) => geofenceMonitoringStatus(item, { ...observed, ...changes }, now);

test('arrival and departure distinguish the initial condition from arming', () => {
  assert.equal(status(), 'Aguardando condição inicial');
  assert.equal(status({ presence: 'inside', confirmedPresence: 'inside', monitoringState: 'waiting_departure' }), 'Aguardando você sair da área');
  assert.equal(status({ presence: 'outside', confirmedPresence: 'outside', monitoringState: 'waiting_arrival' }, { ...rule, transition: 'exit' }), 'Aguardando você entrar na área');
  assert.equal(status({ armed: true, monitoringState: 'armed' }), 'Ativo — aguardando sua chegada');
  assert.equal(status({ armed: true, monitoringState: 'armed' }, { ...rule, transition: 'exit' }), 'Ativo — aguardando sua saída');
  assert.equal(status({ monitoringState: 'armed', armed: false }), 'Aguardando condição inicial');
  assert.equal(geofenceMonitoringStatus({ ...rule, syncStatus: 'active' }, undefined, now), 'Aguardando estado do celular');
});

test('confirmation, uncertainty, permission and installation have independent states', () => {
  assert.equal(status({ armed: true, monitoringState: 'verifying' }), 'Verificando possível chegada');
  assert.equal(status({ armed: true, monitoringState: 'verifying' }, { ...rule, transition: 'exit' }), 'Verificando possível saída');
  assert.equal(status({ monitoringState: 'uncertain', reasonCode: 'accuracy_insufficient' }), 'Localização incerta');
  assert.equal(status({ installationStatus: 'pending_sync', armed: true }), 'Sincronização necessária');
  assert.equal(status({ installationStatus: 'permission_required', armed: true }), 'Precisa de permissão');
  assert.equal(status({ installationStatus: 'failed', armed: true }), 'Falha na instalação');
  assert.equal(status({ armed: true }, { ...rule, leaseUntil: new Date(now - 1).toISOString() }), 'Sincronização necessária');
  assert.equal(status({ armed: true }, { ...rule, status: 'paused' }), 'Pausado');
  assert.equal(status({ armed: true }, { ...rule, status: 'cancelled', syncStatus: 'pending_sync' }), 'Cancelamento pendente no celular');
  assert.match(geofencePresenceReason({ ...observed, monitoringState: 'verifying' }), /tempo limitado/);
});

test('aging a GPS observation preserves the known initial condition and armed state', () => {
  const installed = { ...observed, presence: 'outside', confirmedPresence: 'outside', reasonCode: 'confirmed', armed: true, monitoringState: 'armed', lastObservationAt: now - 121_000, powerSaveMode: true };
  const result = currentGeofenceDiagnostic(rule, [installed], now);
  assert.equal(result.presence, 'unknown');
  assert.equal(result.confirmedPresence, 'outside');
  assert.equal(result.reasonCode, 'stale_location');
  assert.equal(result.armed, true);
  assert.equal(status(result), 'Ativo — aguardando sua chegada');
  assert.equal(installed.presence, 'outside');
  assert.equal(currentGeofenceDiagnostic({ ...rule, revision: 5 }, [installed], now), undefined);
  const waiting = currentGeofenceDiagnostic(rule, [{ ...installed, armed: false, confirmedPresence: 'inside' }], now);
  assert.equal(status(waiting), 'Aguardando você sair da área');
});

test('history explains whether Android delivered a transition, the decision and the occurrence', () => {
  const rows = geofenceDiagnosticHistory([
    { code: 'native_enter_unarmed', source: 'android_event', transition: 'enter', receivedAt: now - 30_000, evidenceAt: now - 40_000, ageMillis: 10_000, regionCurrent: true, armed: false, classification: 'inside', occurrenceCreated: false },
    { code: 'transition_needs_confirmation', source: 'one_shot', transition: 'exit', receivedAt: now, regionCurrent: true, armed: true, classification: 'unknown' },
  ]);
  assert.equal(rows[0].label, 'Transição precisa de confirmação');
  assert.match(rows[0].details, /aquisição pontual.*saída.*região atual.*regra armada.*incerta/);
  assert.equal(rows[1].label, 'Entrada recebida com regra desarmada');
  assert.match(rows[1].details, /evento Android.*entrada.*regra desarmada.*dentro.*sem nova ocorrência.*evidência:.*idade no recebimento: 10 s/);
});

test('history is bounded and renders only known diagnostic fields', () => {
  const arbitrary = 'token-secret stack latitude=10 longitude=20';
  const rows = geofenceDiagnosticHistory(Array.from({ length: 30 }, (_, i) => ({ code: arbitrary, receivedAt: i === 29 ? NaN : now, evidenceAt: Infinity, source: arbitrary, classification: arbitrary, transition: arbitrary, regionCurrent: arbitrary, armed: arbitrary, occurrenceCreated: arbitrary, error: arbitrary })));
  assert.equal(rows.length, 12);
  assert.equal(rows[0].received, 'Instante indisponível');
  assert.equal(rows[0].label, 'Diagnóstico não reconhecido');
  assert.equal(rows[0].details, '');
  assert.doesNotMatch(JSON.stringify(rows), /token-secret|latitude|longitude|stack/);
  assert.equal(geofenceDiagnosticHistory([{ code: 'constructor', receivedAt: now }])[0].label, 'Diagnóstico não reconhecido');
  assert.match(geofencePresenceReason({ ...observed, reasonCode: 'constructor' }), /detalhes/);
  assert.deepEqual(geofenceDiagnosticHistory([null]), []);
  assert.deepEqual(geofenceDiagnosticHistory(undefined), []);
});

test('current native union and configuration decisions retain their closed diagnostic codes', () => {
  const codes = ['native_union_still_inside', 'native_no_place_transition', 'native_transition_unknown', 'native_event_replaced'];
  const rows = geofenceDiagnosticHistory(codes.map(code => ({ code, receivedAt: now, source: 'android_event' })));
  assert.deepEqual(rows.map(row => row.code), [...codes].reverse());
  assert.match(rows[0].label, /instalação anterior/);
  assert.match(rows.at(-1).label, /sem sair do lugar/);
  assert.ok(rows.every(row => row.label !== 'Diagnóstico não reconhecido'));
});

test('bounded confirmation failures distinguish age, precision and contradiction', () => {
  const codes = ['transition_confirmation_old', 'transition_confirmation_insufficient', 'transition_confirmation_contradicted'];
  const rows = geofenceDiagnosticHistory(codes.map(code => ({ code, receivedAt: now, source: 'one_shot', armed: true, occurrenceCreated: false })));
  assert.deepEqual(rows.map(row => row.code), [...codes].reverse());
  assert.match(rows[0].label, /contradiz/);
  assert.match(rows[1].label, /insuficiente/);
  assert.match(rows[2].label, /anterior/);
  assert.ok(rows.every(row => row.details.includes('aquisição pontual') && row.details.includes('sem nova ocorrência')));
});

test('service decisions and local delivery sources stay distinct from location acquisitions', () => {
  const codes = ['native_service_unavailable', 'native_region_limit', 'native_registration_limit', 'native_rule_inactive'];
  const rows = geofenceDiagnosticHistory(codes.map(code => ({ code, receivedAt: now, source: 'android_event' })));
  assert.deepEqual(rows.map(row => row.code), [...codes].reverse());
  assert.ok(rows.every(row => row.label !== 'Diagnóstico não reconhecido'));
  const notification = geofenceDiagnosticHistory([{ code: 'notification_posted', receivedAt: now, source: 'local_notification' }])[0];
  const recovery = geofenceDiagnosticHistory([{ code: 'occurrence_created', receivedAt: now, source: 'local_recovery' }])[0];
  assert.equal(notification.details, 'apresentação da notificação');
  assert.equal(recovery.details, 'recuperação local');
  assert.doesNotMatch(notification.details + recovery.details, /aquisição pontual/);
});
