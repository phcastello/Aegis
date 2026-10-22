import test from 'node:test';
import assert from 'node:assert/strict';
import { loadTypeScript } from './loadTypeScript.mjs';
const { initialConversationId, CURRENT_CONVERSATION_KEY: key } = await loadTypeScript('../src/services/conversationSession.ts');
const storage = () => { const map = new Map([[key, 'old']]); return { getItem: k => map.get(k) ?? null, setItem: (k,v) => map.set(k,v), removeItem: k => map.delete(k) }; };
test('new native process clears selection, same process recreates UI and resumes history', () => {
  const data = storage();
  assert.equal(initialConversationId(data, 'process-one'), null);
  data.setItem(key, 'history-selected');
  assert.equal(initialConversationId(data, 'process-one'), 'history-selected');
  assert.equal(initialConversationId(data, 'process-two'), null);
});
test('web client preserves its previous restoration behavior', () => assert.equal(initialConversationId(storage()), 'old'));
