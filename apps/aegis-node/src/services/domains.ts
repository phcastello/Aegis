import { invoke } from '@tauri-apps/api/core';
export interface RecurrenceRule { frequency: 'daily' | 'weekly' | 'monthly'; interval: number; localTime: string; startDate: string; weekdays?: number[]; monthDay?: number; lastDay?: boolean; untilDate?: string; count?: number }
export type DeliveryPolicy = 'allEligible' | 'bestAvailable';
export interface Reminder { reminderId: string; text: string; dueAt: string; timeZoneId: string; status: string; seriesId: string | null; revision: number; generation: number; deliveryPolicy: DeliveryPolicy; acknowledgedAt?: string | null; openedAt?: string | null; triggeredAt?: string | null }
export interface ReminderDelivery { reminderId: string; generation: number; nodeId: string | null; status: string; providerAcceptedAt: string | null; postedAt: string | null; withdrawnAt: string | null }
export interface ReminderSeries { seriesId: string; text: string; timeZoneId: string; rule: RecurrenceRule; status: string; revision: number; deliveryPolicy: DeliveryPolicy; nextDueAt?: string | null }
export interface Circle { id: string; latitude: number; longitude: number; radiusMeters: number }
export interface Place { id: string; name: string; type: 'property' | 'room' | 'outdoor' | 'other'; parentId: string | null; revision: number; circles: Circle[]; aliases: string[] }
export interface LocationObservation { latitude: number; longitude: number; accuracyMeters: number; capturedAt: string; receivedAt?: string; source?: string; ageSeconds?: number; syncPending?: boolean; placeId?: string | null; nodeId?: string }
export interface NodeLocation { nodeId: string; mode: 'none' | 'fixed' | 'dynamic'; fixedPlaceId: string | null }
export interface LocationSettings { sourceNodeId: string | null; nodes: NodeLocation[]; latest: LocationObservation | null }
export interface LocationPermissions { supported: boolean; providerAvailable?: boolean; foreground?: boolean; precise?: boolean; background?: boolean; servicesEnabled?: boolean; pendingActions?: number; code?: string }
export interface Geofence { id: string; text: string; revision: number; placeIds: string[]; observerNodeId: string; transition: 'enter' | 'exit'; repeat: boolean; timeZoneId?: string; startsAt?: string | null; endsAt?: string | null; status: string; syncStatus: string; leaseUntil?: string; syncedAt?: string | null; errorCode?: string | null }
export interface MapConfig { tileUrl: string | null; attribution: string; geocodingConfigured: boolean; geocoderProvider?: string }
export interface SearchResult { name: string; latitude: number; longitude: number }
type Operation<T extends string> = <R>(operation: T, payload?: object, id?: string) => Promise<R>;
function domain<T extends string>(command: string): Operation<T> { return (operation, payload, id) => invoke(command, { operation, ...(id ? { id } : {}), ...(payload ? { payload } : {}) }); }
export const remindersApi = domain<'list' | 'create' | 'update' | 'cancel' | 'action' | 'seriesCreate' | 'seriesUpdate' | 'seriesPause' | 'seriesResume' | 'seriesSkip' | 'seriesCancel'>('node_reminders');
export const placesApi = domain<'list' | 'create' | 'update' | 'remove' | 'search' | 'config'>('node_places');
export const locationApi = domain<'settings' | 'current' | 'permissions' | 'requestPermission' | 'configureNode' | 'selectSource'>('node_location');
export const geofencesApi = domain<'list' | 'create' | 'update' | 'pause' | 'resume' | 'cancel' | 'sync'>('node_geofences');
export function dateLabel(value?: string | null): string { if (!value) return 'Ainda não definido'; const date = new Date(value); return Number.isNaN(date.getTime()) ? 'Data indisponível' : date.toLocaleString('pt-BR', { dateStyle: 'short', timeStyle: 'short' }); }
export function localInput(value: string) { const date = new Date(value); if (Number.isNaN(date.getTime())) return ''; return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2,'0')}-${String(date.getDate()).padStart(2,'0')}T${String(date.getHours()).padStart(2,'0')}:${String(date.getMinutes()).padStart(2,'0')}`; }
export function recurrenceLabel(rule: RecurrenceRule): string {
  const days = ['seg', 'ter', 'qua', 'qui', 'sex', 'sáb', 'dom'];
  const period = rule.frequency === 'daily' ? (rule.interval === 1 ? 'Diariamente' : `A cada ${rule.interval} dias`) : rule.frequency === 'weekly' ? `${rule.interval === 1 ? 'Semanalmente' : `A cada ${rule.interval} semanas`}, ${(rule.weekdays ?? []).map(day => days[day - 1]).join(', ')}` : `${rule.interval === 1 ? 'Mensalmente' : `A cada ${rule.interval} meses`}, ${rule.lastDay ? 'no último dia' : `dia ${rule.monthDay}`}`;
  return `${period} às ${rule.localTime}${rule.untilDate ? `, até ${rule.untilDate.split('-').reverse().join('/')}` : rule.count ? `, ${rule.count} ocorrências` : ''}`;
}
export const geofenceLabels: Record<string, string> = { pending: 'Pendente de sincronização', pending_sync: 'Pendente de sincronização', waiting_initial: 'Aguardando condição inicial', recoverable_error: 'Precisa de atenção', removed: 'Retirado do celular', pendingSync: 'Pendente de sincronização', installed: 'Instalado no celular', awaitingInitial: 'Aguardando condição inicial', active: 'Ativo no celular', paused: 'Pausado', completed: 'Concluído', cancelled: 'Cancelado', recoverableError: 'Precisa de atenção', error: 'Precisa de atenção', synced: 'Sincronizado', pendingRemoval: 'Cancelamento pendente no celular' };

export function domainError(value: unknown, fallback: string): string { const message = typeof value === 'string' ? value : value instanceof Error ? value.message : null; return message && message.length <= 300 && !/[\r\n\x00-\x1f]/.test(message) ? message : fallback; }
