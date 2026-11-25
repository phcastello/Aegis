import { invoke } from '@tauri-apps/api/core';

export interface HandsFreeState {
  enabled: boolean;
  diagnostic: boolean;
  assistantSelected: boolean;
  permissionGranted: boolean;
  modelReady: boolean;
  modelName?: string | null;
  modelVersion?: string | null;
  modelSha256?: string | null;
  status: string;
  message: string;
  captureConfirmed?: boolean;
  mediaReference?: HandsFreeMediaReference;
  batteryDiagnostics?: HandsFreeBatteryDiagnostics;
  collection?: { enabled: boolean; limitMiB: number; bytes: number; ready: boolean; status: string; skippedEvents: number };
}
export type HandsFreeMediaReferenceStatus = 'unsupported' | 'off' | 'requesting' | 'starting' | 'waiting' | 'warming' | 'ready' | 'unavailable' | 'denied' | 'blocked' | 'stopped';
export interface HandsFreeMediaReference {
  supported: boolean;
  session: boolean;
  status: HandsFreeMediaReferenceStatus;
}
const mediaReferenceStatuses: readonly string[] = ['unsupported', 'off', 'requesting', 'starting', 'waiting', 'warming', 'ready', 'unavailable', 'denied', 'blocked', 'stopped'];
export type HandsFreeBatteryMode = 'off' | 'capture_only' | 'detection';
export interface HandsFreeBatterySummary {
  phoneChargeMah: number | null;
  phoneEnergyMwh?: number | null;
  phoneDischargeMahPerHour?: number | null;
  batteryPercentPoints?: number | null;
  processCpuMs?: number | null;
  captureThreadCpuMs?: number | null;
  detectorCpuMs?: number | null;
  verifierCpuMs?: number | null;
  awakeMs?: number | null;
  screenOnMs?: number | null;
  captureAudioMs?: number | null;
  verifierCandidates?: number | null;
  uidUserCpuMs?: number | null;
  uidSystemCpuMs?: number | null;
  uidAudioTimerMs?: number | null;
}
export interface HandsFreeBatteryRun {
  id: string;
  comparisonKey?: string;
  mode: HandsFreeBatteryMode;
  status: string;
  startedAt: string;
  endedAt: string | null;
  durationMs: number;
  preparationMs?: number | null;
  measurementStartBasis?: 'capture_disabled' | 'first_positive_pcm' | null;
  eligible: boolean;
  issues: string[];
  summary: HandsFreeBatterySummary;
}
export interface HandsFreeBatterySnapshot {
  at: string | null;
  elapsedMs: number | null;
  uptimeMs: number | null;
  processId: string;
  processCpuMs: number | null;
  batteryPercent: number | null;
  chargeMicroAh: number | null;
  energyNanoWh: number | null;
  currentMicroA: number | null;
  currentAverageMicroA?: number | null;
  powerSaveMode?: boolean | null;
  deviceIdleMode?: boolean | null;
  charging?: boolean | null;
  thermalStatus?: number | null;
  sdkInt?: number | null;
  manufacturer?: string | null;
  model?: string | null;
  androidVersion?: string | null;
  plugged: number | null;
  batteryStatus: number | null;
  temperatureDeciC: number | null;
  voltageMv: number | null;
  screenInteractive: boolean | null;
  locked: boolean | null;
  uidHealth: { status: 'ok' | 'unavailable'; scope?: string; userCpuMs: number | null; systemCpuMs: number | null; audioTimerMs: number | null };
}
export interface HandsFreeBatteryAutomation {
  id: string;
  status: 'waiting_screen_off' | 'transitioning' | 'running' | 'completed' | 'cancelled' | 'interrupted';
  mode: HandsFreeBatteryMode | null;
  stageDurationMs: number;
  validation: boolean;
  remainingEstimateMs: number | null;
  preparationElapsedMs?: number | null;
  runIds: string[];
  alarmPrecision?: 'exact' | 'inexact';
  delayMs?: number;
  issue?: string | null;
}
export interface HandsFreeBatteryDiagnostics {
  schema: 1;
  automation?: HandsFreeBatteryAutomation | null;
  active: { id: string; mode: HandsFreeBatteryMode; startedAt: string; elapsedMs: number } | null;
  requiresResume: boolean;
  latest: HandsFreeBatterySnapshot | null;
  runs: HandsFreeBatteryRun[];
}
const batteryModes: readonly string[] = ['off', 'capture_only', 'detection'];
const batteryCode = (value: unknown): value is string => typeof value === 'string' && /^[a-z_]{1,64}$/.test(value);
const batteryId = (value: unknown): value is string => typeof value === 'string' && /^[a-zA-Z0-9_-]{1,80}$/.test(value);
const batteryTime = (value: unknown): value is string => typeof value === 'string' && value.length <= 40 && /T.*(?:Z|[+-]\d{2}:\d{2})$/.test(value) && Number.isFinite(Date.parse(value));
const batteryNumber = (value: unknown, signed = false): value is number => typeof value === 'number' && Number.isFinite(value) && Math.abs(value) <= Number.MAX_SAFE_INTEGER && (signed || value >= 0);
const batteryRecord = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value);
function batterySnapshot(value: unknown): value is HandsFreeBatterySnapshot {
  if (!batteryRecord(value) || !batteryId(value.processId) || !(value.at === null || batteryTime(value.at))) return false;
  const unsigned = ['elapsedMs', 'uptimeMs', 'processCpuMs', 'batteryPercent', 'chargeMicroAh', 'energyNanoWh', 'plugged', 'batteryStatus', 'voltageMv'];
  const signed = ['currentMicroA', 'temperatureDeciC'];
  const optionalNumbers = ['currentAverageMicroA', 'thermalStatus', 'sdkInt'];
  const optionalFlags = ['powerSaveMode', 'deviceIdleMode', 'charging'];
  const optionalStrings = ['manufacturer', 'model', 'androidVersion'];
  // New observations may extend the report; only fields used by this version are interpreted.
  if (
      !optionalNumbers.every(key => value[key] === undefined || value[key] === null || batteryNumber(value[key], key === 'currentAverageMicroA')) ||
      !optionalFlags.every(key => value[key] === undefined || value[key] === null || typeof value[key] === 'boolean') ||
      !optionalStrings.every(key => value[key] === undefined || value[key] === null || (typeof value[key] === 'string' && value[key].length <= 100)) ||
      !unsigned.every(key => value[key] === null || batteryNumber(value[key])) ||
      !signed.every(key => value[key] === null || batteryNumber(value[key], true)) ||
      !['screenInteractive', 'locked'].every(key => value[key] === null || typeof value[key] === 'boolean') ||
      (typeof value.batteryPercent === 'number' && value.batteryPercent > 100)) return false;
  const health = value.uidHealth;
  return batteryRecord(health) && ['ok', 'unavailable'].includes(String(health.status)) &&
    (health.scope === undefined || batteryCode(health.scope)) &&
    ['userCpuMs', 'systemCpuMs', 'audioTimerMs'].every(key => health[key] === null || batteryNumber(health[key]));
}
export function isBatteryAutomationLive(value?: HandsFreeBatteryAutomation | null): boolean {
  return Boolean(value && ['waiting_screen_off', 'transitioning', 'running'].includes(value.status));
}
function batteryAutomation(value: unknown): value is HandsFreeBatteryAutomation {
  return batteryRecord(value) && batteryId(value.id) &&
    ['waiting_screen_off', 'transitioning', 'running', 'completed', 'cancelled', 'interrupted'].includes(String(value.status)) &&
    (value.mode === null || batteryModes.includes(String(value.mode))) && typeof value.validation === 'boolean' &&
    batteryNumber(value.stageDurationMs) && value.stageDurationMs > 0 &&
    (value.remainingEstimateMs === null || batteryNumber(value.remainingEstimateMs)) &&
    (value.preparationElapsedMs === undefined || value.preparationElapsedMs === null || batteryNumber(value.preparationElapsedMs)) &&
    Array.isArray(value.runIds) && value.runIds.length <= 3 && value.runIds.every(batteryId) && new Set(value.runIds).size === value.runIds.length &&
    (value.alarmPrecision === undefined || ['exact', 'inexact'].includes(String(value.alarmPrecision))) &&
    (value.delayMs === undefined || batteryNumber(value.delayMs)) &&
    (value.issue === undefined || value.issue === null || batteryCode(value.issue));
}
function batteryDiagnostics(value: unknown): value is HandsFreeBatteryDiagnostics {
  if (!batteryRecord(value) || value.schema !== 1 || typeof value.requiresResume !== 'boolean' || !Array.isArray(value.runs) || value.runs.length > 8) return false;
  if (value.active !== null) {
    const active = value.active;
    if (!batteryRecord(active) || !batteryId(active.id) || !batteryModes.includes(String(active.mode)) || !batteryTime(active.startedAt) || !batteryNumber(active.elapsedMs)) return false;
  }
  if (value.automation !== undefined && value.automation !== null && !batteryAutomation(value.automation)) return false;
  if (value.latest !== null && !batterySnapshot(value.latest)) return false;
  const summaryKeys = ['phoneChargeMah', 'phoneEnergyMwh', 'phoneDischargeMahPerHour', 'batteryPercentPoints', 'processCpuMs', 'captureThreadCpuMs', 'detectorCpuMs', 'verifierCpuMs', 'awakeMs', 'screenOnMs', 'captureAudioMs', 'verifierCandidates', 'uidUserCpuMs', 'uidSystemCpuMs', 'uidAudioTimerMs'];
  const signedKeys = ['phoneChargeMah', 'phoneEnergyMwh', 'phoneDischargeMahPerHour', 'batteryPercentPoints'];
  const ids = new Set<string>();
  return value.runs.every((run: unknown) => {
    if (!batteryRecord(run) || !batteryId(run.id) || ids.has(run.id) ||
        (run.comparisonKey !== undefined && (typeof run.comparisonKey !== 'string' || !/^[a-f0-9]{64}$/.test(run.comparisonKey))) || !batteryModes.includes(String(run.mode)) || !['active', 'completed', 'interrupted'].includes(String(run.status)) || !batteryTime(run.startedAt) ||
        !(run.endedAt === null || batteryTime(run.endedAt)) || !batteryNumber(run.durationMs) ||
        !(run.preparationMs === undefined || run.preparationMs === null || batteryNumber(run.preparationMs)) ||
        !(run.measurementStartBasis === undefined || run.measurementStartBasis === null || ['capture_disabled', 'first_positive_pcm'].includes(String(run.measurementStartBasis))) || typeof run.eligible !== 'boolean' ||
        !Array.isArray(run.issues) || run.issues.length > 32 || !run.issues.every(batteryCode) || !batteryRecord(run.summary) || !Object.prototype.hasOwnProperty.call(run.summary, 'phoneChargeMah')) return false;
    ids.add(run.id);
    return Object.entries(run.summary).every(([key, number]) => /^[A-Za-z][A-Za-z0-9_]{0,63}$/.test(key) && (number === null || batteryNumber(number, signedKeys.includes(key) || !summaryKeys.includes(key))));
  });
}
/** Automatic comparisons belong to one completed production sequence; quick checks never estimate consumption. */
export function batteryRunsForComparison(runs: readonly HandsFreeBatteryRun[], automation?: HandsFreeBatteryAutomation | null): readonly HandsFreeBatteryRun[] {
  if (!automation) return runs;
  if (automation.status !== 'completed' || automation.validation) return [];
  const selected = new Set(automation.runIds);
  return runs.filter(run => selected.has(run.id));
}
/** Compare only the most recent eligible group. Legacy reports remain readable without inferring compatibility. */
export function latestBatteryRuns(runs: readonly HandsFreeBatteryRun[]): Partial<Record<HandsFreeBatteryMode, HandsFreeBatteryRun>> {
  const eligible = runs.filter(run => run.eligible && run.status === 'completed' && run.endedAt !== null);
  const newest = eligible.reduce<HandsFreeBatteryRun | undefined>((latest, run) =>
    !latest || Date.parse(run.endedAt!) >= Date.parse(latest.endedAt!) ? run : latest, undefined);
  if (!newest) return {};
  const group = newest.comparisonKey ? eligible.filter(run => run.comparisonKey === newest.comparisonKey) : [newest];
  const latest: Partial<Record<HandsFreeBatteryMode, HandsFreeBatteryRun>> = {};
  for (const run of group) {
    const prior = latest[run.mode];
    if (!prior || Date.parse(run.endedAt!) >= Date.parse(prior.endedAt!)) latest[run.mode] = run;
  }
  return latest;
}
export function batteryRateDifference(after?: HandsFreeBatteryRun, before?: HandsFreeBatteryRun): number | null {
  if (!after?.comparisonKey || !before?.comparisonKey || after.comparisonKey !== before.comparisonKey) return null;
  const a = after.summary.phoneDischargeMahPerHour, b = before.summary.phoneDischargeMahPerHour;
  return typeof a === 'number' && typeof b === 'number' ? a - b : null;
}

export type HandsFreeDiagnosticCode = 'command_not_authorized' | 'bridge_unavailable' | 'timeout' | 'invalid_response' | 'native_failure' | 'unsupported' | 'invalid_request' | 'audio_busy' | 'model_invalid' | 'model_runtime_unavailable';
const diagnosticMessages: Record<HandsFreeDiagnosticCode, string> = {
  command_not_authorized: 'A autorização interna da escuta está indisponível nesta versão.',
  bridge_unavailable: 'Não foi possível acessar o controle nativo da escuta.',
  timeout: 'O controle nativo da escuta não respondeu a tempo.',
  invalid_response: 'O controle nativo retornou um estado inválido.',
  native_failure: 'O controle nativo não conseguiu concluir a operação.',
  unsupported: 'Este controle hands-free está indisponível neste dispositivo.',
  invalid_request: 'A configuração de escuta é inválida.',
  model_invalid: 'Escolha o ZIP original do modelo Micro v2 + Open v2.',
  model_runtime_unavailable: 'O Windows não conseguiu carregar o modelo de voz neste computador.',
  audio_busy: 'O áudio está em uso pela sessão hands-free. Aguarde ou cancele a interação.'
};
export class HandsFreeError extends Error {
  constructor(readonly code: HandsFreeDiagnosticCode) { super(diagnosticMessages[code]); this.name = 'HandsFreeError'; }
}
export function handsFreeError(reason: unknown): HandsFreeError {
  if (reason instanceof HandsFreeError) return reason;
  if (reason && typeof reason === 'object' && 'code' in reason && typeof reason.code === 'string' && Object.prototype.hasOwnProperty.call(diagnosticMessages, reason.code))
    return new HandsFreeError(reason.code as HandsFreeDiagnosticCode);
  // Tauri rejects ACL failures before entering the Rust command, with its own message.
  // Recognize only the known closed command; never retain or display the raw exception.
  const message = typeof reason === 'string' ? reason : reason instanceof Error ? reason.message : '';
  if (/\bnode_hands_free\b/i.test(message) && /not allowed|explicitly denied|not permitted/i.test(message))
    return new HandsFreeError('command_not_authorized');
  if (/\bnode_hands_free\b/i.test(message) && /not found/i.test(message)) return new HandsFreeError('bridge_unavailable');
  return new HandsFreeError('native_failure');
}
function stateResponse(value: unknown): HandsFreeState {
  if (!value || typeof value !== 'object') throw new HandsFreeError('invalid_response');
  const row = value as Record<string, unknown>;
  if (!['enabled', 'diagnostic', 'assistantSelected', 'permissionGranted', 'modelReady'].every(key => typeof row[key] === 'boolean') ||
      typeof row.status !== 'string' || !/^[a-z_]{1,64}$/.test(row.status) || typeof row.message !== 'string' || row.message.length > 400 ||
      (row.captureConfirmed !== undefined && typeof row.captureConfirmed !== 'boolean')) throw new HandsFreeError('invalid_response');
  for (const field of ['modelName', 'modelVersion'] as const) {
    if (row[field] !== undefined && row[field] !== null && (typeof row[field] !== 'string' || row[field].length < 1 || row[field].length > 100))
      throw new HandsFreeError('invalid_response');
  }
  if (row.modelSha256 !== undefined && row.modelSha256 !== null && (typeof row.modelSha256 !== 'string' || !/^[a-f0-9]{64}$/.test(row.modelSha256)))
    throw new HandsFreeError('invalid_response');
  if (row.batteryDiagnostics !== undefined && !batteryDiagnostics(row.batteryDiagnostics)) throw new HandsFreeError('invalid_response');
  if (row.mediaReference !== undefined) {
    const ref = row.mediaReference;
    if (!batteryRecord(ref) || typeof ref.supported !== 'boolean' || typeof ref.session !== 'boolean' ||
        !mediaReferenceStatuses.includes(String(ref.status)) || (!ref.supported && ref.session))
      throw new HandsFreeError('invalid_response');
  }
  if (row.collection !== undefined) {
    const c = row.collection as Record<string, unknown>;
    if (!c || typeof c !== 'object' || typeof c.enabled !== 'boolean' || typeof c.ready !== 'boolean' ||
        typeof c.status !== 'string' || !/^[a-z_]{1,64}$/.test(c.status) ||
        typeof c.limitMiB !== 'number' || !Number.isInteger(c.limitMiB) || c.limitMiB < 10 || c.limitMiB > 2048 ||
        typeof c.bytes !== 'number' || !Number.isSafeInteger(c.bytes) || c.bytes < 0 ||
        typeof c.skippedEvents !== 'number' || !Number.isSafeInteger(c.skippedEvents) || c.skippedEvents < 0) throw new HandsFreeError('invalid_response');
  }
  return row as unknown as HandsFreeState;
}
async function invokeControl(args: Record<string, unknown>): Promise<unknown> {
  try { return await invoke<unknown>('node_hands_free', args); }
  catch (reason) { throw handsFreeError(reason); }
}
export type HandsFreeOperation = 'state' | 'configure' | 'permission' | 'assistant' | 'import' | 'export' | 'cancel' | 'collectionConfigure' | 'collectionDelete' | 'batteryStart' | 'batteryFinish' | 'batteryExport' | 'batteryAutoStart' | 'batteryAutoValidate' | 'batteryAutoCancel' | 'mediaReferenceStart' | 'mediaReferenceStop';
export interface HandsFreeOptions { enabled?: boolean; diagnostic?: boolean; collectionEnabled?: boolean; collectionLimitMiB?: number; batteryMode?: HandsFreeBatteryMode }
export async function handsFree(operation: HandsFreeOperation, options?: HandsFreeOptions): Promise<HandsFreeState> {
  return stateResponse(await invokeControl({ operation, ...options }));
}

export async function acquireNativeAudio(kind: 'capture' | 'playback'): Promise<() => Promise<void>> {
  const leaseId = crypto.randomUUID();
  const result = await invokeControl({ operation: 'manualAcquire', leaseId, audioKind: kind });
  if (!result || typeof result !== 'object' || !('acquired' in result) || typeof result.acquired !== 'boolean') throw new HandsFreeError('invalid_response');
  if (!result.acquired) throw new HandsFreeError('audio_busy');
  let released = false;
  return async () => {
    if (released) return;
    released = true;
    await invokeControl({ operation: 'manualRelease', leaseId });
  };
}

/** Server-authorized message IDs only; native code owns credentials, PCM and output policy. */
export const nativeSpeech = {
  async speak(turnId: string, assistantMessageId: string, speechRequestId: string): Promise<void> {
    const result = await invokeControl({ operation: 'playSpeech', turnId, assistantMessageId, speechRequestId });
    if (!result || typeof result !== 'object' || !('played' in result) || result.played !== true) throw new HandsFreeError('invalid_response');
  },
  async stop(speechRequestId: string): Promise<void> {
    await invokeControl({ operation: 'stopSpeech', speechRequestId });
  }
};
