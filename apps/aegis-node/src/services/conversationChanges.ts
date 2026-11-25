import { invoke } from '@tauri-apps/api/core';
export interface ConversationChangeSnapshot {
  generation: string;
  changes: { conversationId: string; revision: number }[];
}
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export async function readConversationChanges(): Promise<ConversationChangeSnapshot> {
  const value = await invoke<unknown>('node_hands_free', { operation: 'conversationChanges' });
  if (!value || typeof value !== 'object') throw Error('invalid_conversation_changes');
  const row = value as Record<string, unknown>;
  if (typeof row.generation !== 'string' || !uuid.test(row.generation) || !Array.isArray(row.changes) || row.changes.length > 128)
    throw Error('invalid_conversation_changes');
  const seen = new Set<string>();
  for (const change of row.changes) {
    if (!change || typeof change !== 'object' || typeof change.conversationId !== 'string' || !uuid.test(change.conversationId) ||
      !Number.isSafeInteger(change.revision) || change.revision <= 0 || seen.has(change.conversationId)) throw Error('invalid_conversation_changes');
    seen.add(change.conversationId);
  }
  return row as unknown as ConversationChangeSnapshot;
}
