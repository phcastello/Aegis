import test from 'node:test';
import assert from 'node:assert/strict';
import { loadTypeScript } from './loadTypeScript.mjs';
const { ConversationSyncQueue, mergeConversationMessages } = await loadTypeScript('../src/services/conversationSync.ts');
const deferred = () => { let resolve, reject; const promise = new Promise((a,b) => { resolve=a; reject=b; }); return {promise,resolve,reject}; };
test('coalesces duplicates and discards an out-of-order snapshot before retrying the newest invalidation', async () => {
  const old=deferred(), fresh=deferred(), applied=[]; let reads=0;
  const queue=new ConversationSyncQueue(() => (++reads === 1 ? old.promise : fresh.promise), (id,row) => {applied.push([id,row]);return true;});
  queue.invalidate('a');queue.invalidate('a');const busy=queue.flush();
  queue.invalidate('a');await queue.flush();assert.equal(reads,1);
  old.resolve('old');await Promise.resolve();fresh.resolve('new');await busy;
  assert.equal(reads,2);assert.deepEqual(applied,[['a','new']]);
  await queue.flush();assert.equal(reads,2);
});
test('active local turn defers reads, failures remain pending, and disposal rejects late results', async () => {
  let blocked=true, fail=true, reads=0;const applied=[];
  const queue=new ConversationSyncQueue(async () => {reads++;if(fail)throw Error();return 'persisted';},(_,row)=>{applied.push(row);return true;},()=>blocked);
  queue.invalidate('a');await queue.flush();assert.equal(reads,0);
  blocked=false;await queue.flush();assert.equal(reads,1);assert.equal(queue.pending('a'),true);
  fail=false;await queue.flush();assert.deepEqual(applied,['persisted']);
  const held=deferred();const second=new ConversationSyncQueue(()=>held.promise,()=>{assert.fail('disposed result applied');});
  second.invalidate('b');const busy=second.flush();second.dispose();held.resolve('late');await busy;
});
test('server message IDs merge incrementally, retain local objects and avoid duplicate accepted users', () => {
  const user={id:'local-user',conversationId:'a',role:'user',content:'Pedido',createdAt:'1',pending:false};
  const assistant={id:'local-assistant',serverId:'answer',conversationId:'a',role:'assistant',content:'Parcial',createdAt:'2',pending:false};
  const incoming=[{id:'request',conversationId:'a',role:'user',content:'Pedido',createdAt:'1'},
    {id:'answer',conversationId:'a',role:'assistant',content:'Completa',createdAt:'2'},
    {id:'next',conversationId:'a',role:'assistant',content:'Nova mensagem',createdAt:'3'}];
  let rows=mergeConversationMessages([user,assistant],incoming);rows=mergeConversationMessages(rows,incoming);
  assert.equal(rows.length,3);assert.equal(rows[0],user);assert.equal(user.id,'local-user');assert.equal(user.serverId,'request');
  assert.equal(rows[1],assistant);assert.equal(assistant.content,'Completa');
});
test('pending/streaming and interrupted local messages are never overwritten or dropped', () => {
  const pending={id:'local',serverId:'server',conversationId:'a',role:'assistant',content:'Locais',createdAt:'1',streaming:true};
  const interrupted={id:'interrupted',conversationId:'a',role:'assistant',content:'Interrompida',createdAt:'2',interrupted:true};
  const result=mergeConversationMessages([pending,interrupted],[{...pending,id:'server',content:'Obsoleta'}]);
  assert.equal(result[0],pending);assert.equal(pending.content,'Locais');assert.equal(result[1],interrupted);
});

test('identity/process generation reset rejects an old in-flight result and accepts a fresh revision', async () => {
  const old=deferred(),applied=[];let reads=0;
  const queue=new ConversationSyncQueue(()=>++reads===1?old.promise:Promise.resolve('new identity'),(_,row)=>{applied.push(row);return true;});
  queue.invalidate('a');const pending=queue.flush();queue.reset();queue.invalidate('a');await queue.flush();old.resolve('old identity');await pending;await queue.flush();await Promise.resolve();
  assert.deepEqual(applied,['new identity']);
});

test('accepted user interrupted before assistant ID reconciles once between known anchors', () => {
  const history={id:'history',serverId:'history',role:'assistant',content:'Anterior'};
  const first={id:'local-first',role:'user',content:'Mesmo pedido',pending:false};
  const interrupted={id:'local-interrupted',role:'assistant',content:'Parcial',interrupted:true};
  const second={id:'local-second',role:'user',content:'Mesmo pedido',pending:false};
  const remote=[{...history},{id:'user-first',role:'user',content:'Mesmo pedido'},{id:'user-second',role:'user',content:'Mesmo pedido'}];
  const merged=mergeConversationMessages([history,first,interrupted,second],remote);
  assert.equal(merged.filter(row=>row.role==='user').length,2);assert.equal(first.serverId,'user-first');assert.equal(second.serverId,'user-second');
  assert.equal(merged[2],interrupted);
});

test('a large replay never runs more than three HTTP reads, even with concurrent flush calls', async () => {
  let active=0,maximum=0;const applied=[];const gates=[];
  const queue=new ConversationSyncQueue(id=>new Promise(resolve=>{active++;maximum=Math.max(maximum,active);gates.push(()=>{active--;resolve(id);});}),(_,id)=>{applied.push(id);return true;});
  for(let i=0;i<12;i++)queue.invalidate(String(i));
  const running=queue.flush();await queue.flush();assert.equal(active,3);
  while(applied.length<12){const ready=gates.splice(0);for(const release of ready)release();await new Promise(resolve=>setImmediate(resolve));}
  await running;assert.equal(maximum,3);assert.equal(new Set(applied).size,12);
});
