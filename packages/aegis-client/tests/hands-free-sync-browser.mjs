// Real Vue/Chromium + HTTP; native identity/event journal is simulated. No device evidence.
import assert from 'node:assert/strict';
import http from 'node:http';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../..');
const requireNative=createRequire(join(root,'apps/aegis-node/package.json'));
const {createServer}=await import(requireNative.resolve('vite'));
const a='10000000-0000-4000-8000-000000000001', b='20000000-0000-4000-8000-000000000002', c='30000000-0000-4000-8000-000000000003';
const generation='90000000-0000-4000-8000-000000000009';
const date='2026-10-01T12:00:00Z';
const data=new Map([a,b].map((id,index)=>[id,{id,title:index?'Beta':'Alfa',createdAt:date,updatedAt:date,titleSource:'manual',messages:[{id:`${id}-initial`,conversationId:id,role:'assistant',createdAt:date,content:Array.from({length:35},(_,i)=>`Parágrafo ${i} do histórico ${index?'Beta':'Alfa'}.`).join('\n\n')}]}]));
const summary=row=>({...row,messages:undefined,messageCount:row.messages.length,lastMessagePreview:row.messages.at(-1)?.content});
let hold=null, held=null, failReads=0, stream=null, reads=0, heads=0;
const delay=ms=>new Promise(r=>setTimeout(r,ms));
async function until(fn,label){for(let i=0;i<200;i++){if(fn())return;await delay(20);}throw Error(`timeout: ${label}`);}
const vite=await createServer({root:join(root,'apps/aegis-node'),server:{host:'127.0.0.1',port:0,strictPort:false}});await vite.listen();
const viteOrigin=`http://127.0.0.1:${vite.httpServer.address().port}`;
const json=(res,value,status=200)=>{res.writeHead(status,{'Content-Type':'application/json','Cache-Control':'no-store'});res.end(JSON.stringify(value));};
const server=http.createServer(async(req,res)=>{
  const url=new URL(req.url,'http://localhost');const path=url.pathname;
  if(!path.startsWith('/api/')){const response=await fetch(viteOrigin+req.url);const headers=Object.fromEntries(response.headers);for(const name of ['content-encoding','content-length','transfer-encoding'])delete headers[name];res.writeHead(response.status,headers);res.end(Buffer.from(await response.arrayBuffer()));return;}
  let raw='';for await(const chunk of req)raw+=chunk;const body=raw?JSON.parse(raw):{};
  if(path==='/api/health')return json(res,{status:'ok'});
  if(path==='/api/voice/status')return json(res,{enabled:false,available:false});
  if(path==='/api/voice/transcription/status')return json(res,{enabled:false,configured:false});
  if(path==='/api/email/status')return json(res,{isConnected:false});
  if(path==='/api/chat/proactive-activity')return json(res,[]);
  if(path==='/api/chat/conversations'){heads++;return json(res,{items:[...data.values()].map(summary),hasMore:false});}
  if(path==='/api/chat/messages/stream'){
    res.writeHead(200,{'Content-Type':'application/x-ndjson'});res.flushHeaders();
    const row=data.get(body.conversationId);
    row.messages.push({id:`${body.turnId}-user`,conversationId:row.id,role:'user',content:body.content,createdAt:new Date().toISOString()});
    const event=value=>res.write(JSON.stringify({turnId:body.turnId,...value})+'\n');
    event({type:'conversation',conversationId:row.id});event({type:'token',content:'Resposta local em andamento. '});
    stream={body,done(){const id=`${body.turnId}-assistant`;row.messages.push({id,conversationId:row.id,role:'assistant',content:'Resposta local em andamento. ',createdAt:new Date().toISOString()});row.updatedAt=new Date().toISOString();event({type:'done',conversationId:row.id,messageId:id,conversationTitle:row.title});res.end();}};
    return;
  }
  if(path.match(/^\/api\/chat\/turns\//))return json(res,null,204);
  const match=path.match(/^\/api\/chat\/conversations\/([^/]+)(\/title)?$/);
  if(match){
    const row=data.get(match[1]);if(!row)return json(res,{},404);
    if(match[2]){row.title=body.title;row.titleSource='manual';row.updatedAt=new Date().toISOString();return json(res,summary(row));}
    reads++;if(failReads>0){failReads--;return json(res,{error:'temporary'},503);}
    const snapshot=structuredClone(row);
    if(hold===row.id){hold=null;await new Promise(release=>{held={release,id:row.id};});held=null;}
    return json(res,snapshot);
  }
  if(path.endsWith('/memory-activity'))return json(res,{pending:false,sections:[]});
  return json(res,{},404);
});await new Promise(r=>server.listen(0,'127.0.0.1',r));const origin=`http://127.0.0.1:${server.address().port}`;
const browser=await chromium.launch({executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE,headless:true});
const context=await browser.newContext({viewport:{width:390,height:900},isMobile:true,hasTouch:true});
await context.addInitScript(({origin,generation})=>{
  localStorage.setItem('aegis.voice.autoSpeak','false');window.fixtureJournal={generation,changes:[]};
  window.__TAURI_INTERNALS__={invoke:async(command,args)=>{
    if(command==='runtime_info')return {platform:'android',backendUrl:origin,version:'0.8.0',processSessionId:'sync-test'};
    if(command==='node_hands_free'&&args.operation==='conversationChanges'){if(window.fixtureJournalFailures>0){window.fixtureJournalFailures--;throw Error('temporary native bridge');}return structuredClone(window.fixtureJournal);}
    if(command==='node_read_visibility')return true;
    if(command==='node_status')return {state:'unpaired',node:null};
    if(command==='node_transport_status')return {transportState:'online'};
    if(command==='node_notification_settings')return {granted:false,pushConfigured:false,pendingActions:0};
    if(command==='node_consume_reminder_open')return {reminderId:null,conversationId:null};
    if(command==='check_android_update')return null;
  }};
},{origin,generation});
const page=await context.newPage();page.setDefaultTimeout(10000);const errors=[];page.on('pageerror',e=>errors.push(e.message));
const composer=()=>page.locator('.composer textarea');
const history=async()=>{if(!await page.locator('#conversation-sidebar').evaluate(el=>!el.inert))await page.getByRole('button',{name:'Abrir histórico',exact:true}).click();};
const select=async title=>{await history();await page.locator('.history-item__main').filter({hasText:title}).click();await page.waitForFunction(title=>document.querySelector('.chat-header h1')?.textContent===title,title);};
let revision=0;
async function publish(id,{notify=true,sequence=++revision}={}){
  await page.evaluate(({id,sequence,notify})=>{const rows=window.fixtureJournal.changes;const previous=rows.find(row=>row.conversationId===id);if(previous)previous.revision=Math.max(sequence,previous.revision);else rows.push({conversationId:id,revision:sequence});if(notify)window.dispatchEvent(new CustomEvent('aegis-conversation-changed',{detail:{conversationId:id,revision:sequence}}));},{id,sequence,notify});
}
function append(id,text){const row=data.get(id);row.messages.push({id:crypto.randomUUID(),conversationId:id,role:'assistant',content:text,createdAt:new Date().toISOString()});row.updatedAt=new Date().toISOString();}
const visible=text=>page.locator('.messages').getByText(text,{exact:true});
let assertions=0;const equal=(actual,expected,label)=>{assert.equal(actual,expected,label);assertions++;};
try{
  await page.goto(origin);await composer().waitFor();await select('Beta');await composer().fill('Rascunho intacto');
  await page.locator('.messages').evaluate(el=>{el.dispatchEvent(new WheelEvent('wheel',{deltaY:-100,bubbles:true}));el.scrollTop=180;});await delay(80);
  const reading=await page.locator('.messages').evaluate(el=>el.scrollTop);
  data.set(c,{id:c,title:'Conversa hands-free',titleSource:'manual',createdAt:date,updatedAt:new Date().toISOString(),messages:[]});append(c,'Primeira resposta hands-free.');
  const headsBefore=heads;await publish(c);await history();await page.locator('.history-item__main').filter({hasText:'Conversa hands-free'}).waitFor();
  equal((await page.locator('.chat-header h1').textContent()).trim(),'Beta','new native conversation never navigates');equal(await composer().inputValue(),'Rascunho intacto','draft survives native creation');equal(heads,headsBefore,'native invalidation fetches only its destination');
  await select('Conversa hands-free');await composer().fill('Rascunho hands-free');append(c,'Mensagem incremental.');await publish(c);await visible('Mensagem incremental.').waitFor();
  await publish(c,{sequence:revision});await publish(c,{sequence:revision-1});await delay(160);equal(await visible('Mensagem incremental.').count(),1,'duplicate/out-of-order events never duplicate a persisted message');equal(await composer().inputValue(),'Rascunho hands-free','selected chat draft survives incremental merge');
  for(let i=0;i<5;i++){append(c,`Interação consecutiva ${i}.`);await publish(c);}await visible('Interação consecutiva 4.').waitFor();equal(await page.locator('.messages').getByText('Interação consecutiva',{exact:false}).count(),5,'consecutive interactions all arrive exactly once');
  append(b,'Mudança com aplicativo em segundo plano.');await publish(b,{notify:false});await page.evaluate(()=>window.dispatchEvent(new Event('aegis-app-visibility')));await delay(400);await select('Beta');await visible('Mudança com aplicativo em segundo plano.').waitFor();equal(await composer().inputValue(),'Rascunho intacto','resume preserves unrelated draft');equal(Math.round(await page.locator('.messages').evaluate(el=>el.scrollTop)),Math.round(reading),'resume preserves reading position');
  failReads=1;append(b,'Recuperada após falha temporária.');await publish(b);await until(()=>failReads===0,'failed reconciliation');await page.evaluate(()=>window.dispatchEvent(new Event('online')));await visible('Recuperada após falha temporária.').waitFor();equal(await visible('Recuperada após falha temporária.').count(),1,'online replays a failed fetch without consuming invalidation');
  hold=b;append(b,'Snapshot antes da próxima alteração.');await publish(b);await until(()=>!!held,'stale invalidation fetch');append(b,'Snapshot mais recente.');await publish(b);held.release();await visible('Snapshot mais recente.').waitFor();equal(await visible('Snapshot antes da próxima alteração.').count(),1,'late snapshots cannot overwrite a newer invalidation');
  // Concurrent navigation applies to the original context, never the selected one.
  hold=b;append(b,'Chegou durante navegação.');await publish(b);await until(()=>!!held,'navigation invalidation fetch');await select('Conversa hands-free');held.release();await delay(180);equal(await visible('Chegou durante navegação.').count(),0,'late fetch does not enter another conversation');equal(await composer().inputValue(),'Rascunho hands-free','navigation draft remains local');await select('Beta');await visible('Chegou durante navegação.').waitFor();
  // A rename completed while a snapshot is in flight must win against its old metadata.
  hold=b;append(b,'Antes de renomear.');await publish(b);await until(()=>!!held,'rename invalidation fetch');await page.getByRole('button',{name:'Menu da conversa',exact:true}).click();await page.getByRole('menuitem',{name:'Renomear',exact:true}).click();const rename=page.getByRole('dialog',{name:'Renomear conversa',exact:true});await rename.locator('input').fill('Beta renomeada');await rename.getByRole('button',{name:'Salvar',exact:true}).click();await rename.waitFor({state:'hidden'});held.release();await delay(300);equal((await page.locator('.chat-header h1').textContent()).trim(),'Beta renomeada','late metadata does not undo local rename');
  // A newer navigation read also supersedes an older native snapshot of the same context.
  hold=b;append(b,'Antes da leitura de navegação.');await publish(b);await until(()=>!!held,'older sync vs navigation');append(b,'Leitura de navegação mais recente.');await select('Conversa hands-free');await select('Beta renomeada');await visible('Leitura de navegação mais recente.').waitFor();held.release();await delay(260);equal(await visible('Leitura de navegação mais recente.').count(),1,'newer navigation snapshot wins against older native fetch');
  // Remote invalidation waits while a local turn owns transient messages.
  await composer().fill('Turno local concorrente');await page.getByRole('button',{name:'Enviar mensagem',exact:true}).click();await until(()=>!!stream,'active local stream');await visible('Resposta local em andamento.').waitFor();append(b,'Hands-free durante turno local.');const readsBefore=reads;await publish(b);await delay(180);equal(reads,readsBefore,'native reconciliation defers while a local turn is active');equal(await visible('Resposta local em andamento.').count(),1,'local streaming message remains intact');stream.done();await visible('Hands-free durante turno local.').waitFor();equal(await visible('Turno local concorrente').count(),1,'completed local prompt deduplicates by its persisted assistant anchor');equal(await visible('Resposta local em andamento.').count(),1,'completed local response keeps its single UI row');
  await page.evaluate(()=>{window.fixtureJournalFailures=1;});append(b,'Recuperada após falha da ponte nativa.');await publish(b);await visible('Recuperada após falha da ponte nativa.').waitFor();equal(await visible('Recuperada após falha da ponte nativa.').count(),1,'bounded retry recovers a native snapshot failure while app stays open');
  // A native process/identity epoch can restart its revision counter independently.
  await page.evaluate(()=>{window.fixtureJournal={generation:'80000000-0000-4000-8000-000000000008',changes:[]};});append(b,'Nova geração nativa.');await publish(b,{sequence:1});await visible('Nova geração nativa.').waitFor();equal(await visible('Nova geração nativa.').count(),1,'journal epoch reset accepts lower revision after restart');
  equal(errors.length,0,'no Vue/browser errors');console.log(JSON.stringify({suite:'hands-free-sync-browser',assertions,result:'passed',evidence:'Chromium + Vue + HTTP, simulated Android journal'}));
}finally{held?.release();await context.close();await browser.close();server.closeAllConnections();await new Promise(r=>server.close(r));await vite.close();}
