import { invoke } from '@tauri-apps/api/core';
import { validateWindowsUpdate } from './updateMetadata.js';
import type { Platform } from './runtime.js';

export interface AvailableUpdate {
  version: string;
  kind: 'installer' | 'apk';
  prepare?(onProgress: (received: number, total?: number) => void): Promise<void>;
  dispose?(): Promise<void>;
  install(onProgress: (received: number, total?: number) => void): Promise<void | { status: 'permission_required' | 'installer_opened' }>;
}

export async function checkUpdate(platform: Platform): Promise<AvailableUpdate | null> {
  if (platform === 'windows') {
    const { check } = await import('@tauri-apps/plugin-updater');
    const update = await check({ timeout: 10000 });
    if (!update) return null;
    validateWindowsUpdate(update.rawJson, update.version);
    return {
      version: update.version, kind: 'installer',
      dispose: () => update.close(),
      async prepare(onProgress) {
        let received = 0;
        let total: number | undefined;
        await update.download(event => {
          if (event.event === 'Started') total = event.data.contentLength;
          if (event.event === 'Progress') { received += event.data.chunkLength; onProgress(received, total); }
        });
      },
      // Windows exits only after this separately confirmed installer handoff.
      install: () => update.install()
    };
  }
  if (platform === 'android') {
    const update = await invoke<{ version: string; url: string } | null>('check_android_update');
    if (!update) return null;
    return { version: update.version, kind: 'apk', install: () => invoke<{status:'permission_required'|'installer_opened'}>('install_android_update', {version:update.version}) };
  }
  return null;
}
