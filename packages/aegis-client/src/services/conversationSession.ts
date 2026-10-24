export const CURRENT_CONVERSATION_KEY = 'aegis.currentConversationId';
const PROCESS_KEY = 'aegis.conversationProcessSession';
// A Rust process UUID survives Activity/WebView recreation, but never a process restart.
export function initialConversationId(storage: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>, processId?: string): string | null {
  if (processId && storage.getItem(PROCESS_KEY) !== processId) {
    storage.removeItem(CURRENT_CONVERSATION_KEY);
    storage.setItem(PROCESS_KEY, processId);
  }
  return storage.getItem(CURRENT_CONVERSATION_KEY);
}
