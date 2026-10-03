import { invoke } from '@tauri-apps/api/core';
export type NodeState = 'unpaired' | 'pairing' | 'paired' | 'disabled' | 'revoked' | 'pairingError';
export interface NodeView {
  id: string; name: string; platform: 'android' | 'windows'; enabled: boolean;
  appVersion: string; protocolVersion: number; pairedAt: string;
  availability?: 'online' | 'offline'; lastSeenAt?: string | null; lastHeartbeatAt?: string | null;
  createdAt: string; updatedAt: string | null; revokedAt: string | null;
}
export interface IdentityStatus { state: NodeState; node: NodeView | null; error: string | null }
export interface PairingCode { code: string; expiresAt: string }
export interface NodeServices {
  status(): Promise<IdentityStatus>;
  pair(name: string, code: string): Promise<IdentityStatus>;
  list(): Promise<NodeView[]>;
  rename(id: string, name: string): Promise<NodeView>;
  setEnabled(id: string, enabled: boolean): Promise<NodeView>;
  revoke(id: string): Promise<NodeView>;
  createPairingCode(): Promise<PairingCode>;
}
// Credentials and recovery keys are never IPC arguments/results.
export const nodeServices: NodeServices = {
  status: () => invoke('node_status'), pair: (name, code) => invoke('node_pair', { name, code }),
  list: () => invoke('node_list'), rename: (id, name) => invoke('node_rename', { id, name }),
  setEnabled: (id, enabled) => invoke('node_set_enabled', { id, enabled }),
  revoke: (id) => invoke('node_revoke', { id }), createPairingCode: () => invoke('node_create_pairing_code')
};

export interface TransportStatus { transportState: 'connecting' | 'online' | 'reconnecting' | 'offline'; lastError: string | null }
export const transportServices = {
  status: (): Promise<TransportStatus> => invoke('node_transport_status'),
  reconnect: (): Promise<void> => invoke('node_transport_reconnect')
};
