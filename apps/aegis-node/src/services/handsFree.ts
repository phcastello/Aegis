import { invoke } from '@tauri-apps/api/core';

export interface HandsFreeState {
  enabled: boolean;
  diagnostic: boolean;
  assistantSelected: boolean;
  permissionGranted: boolean;
  modelReady: boolean;
  status: string;
  message: string;
  captureConfirmed?: boolean;
  collection?: { enabled: boolean; limitMiB: number; bytes: number; ready: boolean; status: string; skippedEvents: number };
}
export type HandsFreeDiagnosticCode = 'command_not_authorized' | 'bridge_unavailable' | 'timeout' | 'invalid_response' | 'native_failure' | 'unsupported' | 'invalid_request' | 'audio_busy';
const diagnosticMessages: Record<HandsFreeDiagnosticCode, string> = {
  command_not_authorized: 'A autorização interna da escuta está indisponível nesta versão.',
  bridge_unavailable: 'Não foi possível acessar o controle nativo da escuta.',
  timeout: 'O controle nativo da escuta não respondeu a tempo.',
  invalid_response: 'O controle nativo retornou um estado inválido.',
  native_failure: 'O controle nativo não conseguiu concluir a operação.',
  unsupported: 'Este controle hands-free está disponível no Android.',
  invalid_request: 'A configuração de escuta é inválida.',
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
export type HandsFreeOperation = 'state' | 'configure' | 'permission' | 'assistant' | 'import' | 'export' | 'cancel' | 'collectionConfigure' | 'collectionDelete';
export interface HandsFreeOptions { enabled?: boolean; diagnostic?: boolean; collectionEnabled?: boolean; collectionLimitMiB?: number }
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
