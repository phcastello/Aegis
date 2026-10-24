import { Channel, invoke, Resource } from '@tauri-apps/api/core';
import { validateWindowsUpdate, type DistributionDetails } from './updateMetadata.js';
import type { RuntimeInfo } from './runtime.js';
import type { UpdatePhase } from './updateDiagnostics.js';

type InstallResult = { status: 'permission_required' | 'installer_opened' } | { status: 'failed'; phase: UpdatePhase; code: string };

export interface AvailableUpdate extends Partial<Omit<DistributionDetails, 'version'>> {
  version: string;
  kind: 'installer' | 'apk';
  prepare?(onProgress: (received: number, total?: number) => void): Promise<void>;
  dispose?(): Promise<void>;
  install(onProgress: (received: number, total?: number) => void): Promise<void | InstallResult>;
}

export async function checkUpdate(runtime: RuntimeInfo): Promise<AvailableUpdate | null> {
  const platform = runtime.platform;
  if (platform === 'windows') {
    const { check } = await import('@tauri-apps/plugin-updater');
    const update = await check({ timeout: 10000 });
    if (!update) return null;
    let details: DistributionDetails;
    try {
      details = validateWindowsUpdate(update.rawJson, update.version, runtime);
    } catch (error) {
      await update.close();
      throw error;
    }
    let verifiedBytes: Resource | null = null;
    return {
      ...details, kind: 'installer',
      async dispose() {
        try { await verifiedBytes?.close(); }
        finally { verifiedBytes = null; await update.close(); }
      },
      async prepare(onProgress) {
        if (verifiedBytes) return;
        const progress = new Channel<{ received: number; total?: number | null }>();
        progress.onmessage = event => onProgress(event.received, event.total ?? undefined);
        const bytesRid = await invoke<number>('prepare_windows_update', {
          updateRid: update.rid, onProgress: progress
        });
        // Rust keeps these exact bytes only after Tauri's signature and the
        // manifest SHA-256 both pass. No second transfer precedes installation.
        verifiedBytes = new Resource(bytesRid);
      },
      // Windows exits only after this separately confirmed installer handoff.
      async install() {
        if (!verifiedBytes) throw new Error('update_archive_invalid');
        await invoke<void>('install_windows_update', { updateRid: update.rid, bytesRid: verifiedBytes.rid });
        verifiedBytes = null;
      }
    };
  }
  if (platform === 'android') {
    const update = await invoke<Omit<DistributionDetails, 'distributionKind'> & { url: string; kind: 'normal' | 'rollback' } | null>('check_android_update');
    if (!update) return null;
    return { ...update, distributionKind: update.kind, kind: 'apk', install: () => invoke<InstallResult>('install_android_update', {version:update.version}) };
  }
  return null;
}
