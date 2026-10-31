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
  return row as unknown as HandsFreeState;
}
async function invokeControl(args: Record<string, unknown>): Promise<unknown> {
  try { return await invoke<unknown>('node_hands_free', args); }
  catch (reason) { throw handsFreeError(reason); }
}
export type HandsFreeOperation = 'state' | 'configure' | 'permission' | 'assistant' | 'import' | 'export' | 'cancel';
export async function handsFree(operation: HandsFreeOperation, options?: { enabled: boolean; diagnostic: boolean }): Promise<HandsFreeState> {
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
