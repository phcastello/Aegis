import { invoke } from '@tauri-apps/api/core';

export interface HandsFreeState {
  enabled: boolean;
  diagnostic: boolean;
  assistantSelected: boolean;
  permissionGranted: boolean;
  modelReady: boolean;
  status: string;
  message: string;
}
export type HandsFreeOperation = 'state' | 'configure' | 'permission' | 'assistant' | 'import' | 'export' | 'cancel';
export function handsFree(operation: HandsFreeOperation, options?: { enabled: boolean; diagnostic: boolean }): Promise<HandsFreeState> {
  return invoke<HandsFreeState>('node_hands_free', { operation, ...options });
}

export async function acquireNativeAudio(kind: 'capture' | 'playback'): Promise<() => Promise<void>> {
  const leaseId = crypto.randomUUID();
  const result = await invoke<{ acquired: boolean }>('node_hands_free', { operation: 'manualAcquire', leaseId, audioKind: kind });
  if (!result.acquired) throw new Error('O microfone está em uso pela sessão hands-free. Aguarde ou cancele a sessão em Áudio e voz.');
  let released = false;
  return async () => {
    if (released) return;
    released = true;
    await invoke('node_hands_free', { operation: 'manualRelease', leaseId });
  };
}
