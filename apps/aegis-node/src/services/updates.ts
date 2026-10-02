import { invoke } from '@tauri-apps/api/core';
import { openUrl } from '@tauri-apps/plugin-opener';
import { validateWindowsUpdate } from './updateMetadata.js';
import type { Platform } from './runtime.js';

export interface AvailableUpdate {
  version: string;
  kind: 'installer' | 'apk';
  install(onProgress: (received: number, total?: number) => void): Promise<void>;
}

export async function checkUpdate(platform: Platform): Promise<AvailableUpdate | null> {
  if (platform === 'windows') {
    const { check } = await import('@tauri-apps/plugin-updater');
    const update = await check({ timeout: 10000 });
    if (!update) return null;
    validateWindowsUpdate(update.rawJson, update.version);
    return {
      version: update.version, kind: 'installer',
      async install(onProgress) {
        let received = 0;
        let total: number | undefined;
        await update.downloadAndInstall(event => {
          if (event.event === 'Started') total = event.data.contentLength;
          if (event.event === 'Progress') { received += event.data.chunkLength; onProgress(received, total); }
        });
        const { relaunch } = await import('@tauri-apps/plugin-process');
        await relaunch();
      }
    };
  }
  if (platform === 'android') {
    const update = await invoke<{ version: string; url: string } | null>('check_android_update');
    if (!update) return null;
    return { version: update.version, kind: 'apk', install: () => openUrl(update.url) };
  }
  return null;
}
