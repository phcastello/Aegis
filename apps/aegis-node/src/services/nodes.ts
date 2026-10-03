import { invoke } from '@tauri-apps/api/core';
export type NodeState = 'unpaired' | 'pairing' | 'paired' | 'disabled' | 'revoked' | 'pairingError';
export interface NodeView {
  id: string; name: string; platform: 'android' | 'windows'; enabled: boolean;
  appVersion: string; protocolVersion: number; pairedAt: string;
  availability?: 'online' | 'backgroundReachable' | 'offline'; lastSeenAt?: string | null; lastHeartbeatAt?: string | null;
  capabilities?: { name: string; version: number }[]; targetPriority?: number;
  createdAt: string; updatedAt: string | null; revokedAt: string | null;
}
export interface IdentityStatus { state: NodeState; node: NodeView | null; error: string | null }
export interface PairingCode { code: string; expiresAt: string }
export interface TargetRequest { requiredCapabilities: { name: string; minimumVersion: number }[]; preferredNodeId?: string | null }
export interface TargetResult { node: { id: string; name: string } | null; code: string | null; onlineNodes: number; capabilityCompatibleNodes: number }
export interface NodeServices {
  status(): Promise<IdentityStatus>;
  pair(name: string, code: string): Promise<IdentityStatus>;
  list(): Promise<NodeView[]>;
  rename(id: string, name: string): Promise<NodeView>;
  setEnabled(id: string, enabled: boolean): Promise<NodeView>;
  resolve(request: TargetRequest): Promise<TargetResult>;
  setPriority(id: string, priority: number): Promise<NodeView>;
  revoke(id: string): Promise<NodeView>;
  createPairingCode(): Promise<PairingCode>;
}
// Credentials and recovery keys are never IPC arguments/results.
export const nodeServices: NodeServices = {
  status: () => invoke('node_status'), pair: (name, code) => invoke('node_pair', { name, code }),
  list: () => invoke('node_list'), rename: (id, name) => invoke('node_rename', { id, name }),
  setEnabled: (id, enabled) => invoke('node_set_enabled', { id, enabled }),
  resolve: (request) => invoke('node_resolve_target', { request }),
  setPriority: (id, priority) => invoke('node_set_target_priority', { id, priority }),
  revoke: (id) => invoke('node_revoke', { id }), createPairingCode: () => invoke('node_create_pairing_code')
};

export interface TransportStatus { transportState: 'connecting' | 'online' | 'reconnecting' | 'offline'; lastError: string | null; lastDiagnostic?: { phase: string; reason: string; httpStatus: number | null; closeCode: number | null } | null }
export const transportServices = {
  status: (): Promise<TransportStatus> => invoke('node_transport_status'),
  reconnect: (): Promise<void> => invoke('node_transport_reconnect')
};

export interface NotificationSettings { granted: boolean; pushConfigured: boolean; autostart: boolean }
export interface NotificationResult { node: {id:string;name:string}|null; reachability: string; transport: string|null; status:string; commandId:string|null }
export const notificationServices = {
 test: (id:string):Promise<NotificationResult> => invoke('node_test_notification',{id}),
 settings: ():Promise<NotificationSettings> => invoke('node_notification_settings'),
 permission: ():Promise<void> => invoke('node_request_notification_permission'),
 autostart: (enabled:boolean):Promise<void> => invoke('node_set_autostart',{enabled})
};
