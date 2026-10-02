import { invoke } from '@tauri-apps/api/core';

export type Platform = 'android' | 'windows' | 'unsupported';
export type BackendStatus = 'connected' | 'unavailable';

export interface RuntimeInfo {
  platform: Platform;
  backendUrl: string | null;
}

export interface HealthResult {
  status: BackendStatus;
  message: string | null;
}

export interface DiagnosticServices {
  runtimeInfo(): Promise<RuntimeInfo>;
  checkBackend(): Promise<HealthResult>;
}

// The WebView cannot supply URLs, methods or headers to the native HTTP client.
export const diagnosticServices: DiagnosticServices = {
  runtimeInfo: () => invoke<RuntimeInfo>('runtime_info'),
  checkBackend: () => invoke<HealthResult>('check_backend')
};
