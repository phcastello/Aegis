import assert from 'node:assert/strict';
import { test } from 'node:test';
import { reloadReadyWindowsPage } from './windows-webview-ready.mjs';
function pageFixture(log,reloadFailure) {return {async waitForFunction(_fn,_arg,options){assert.ok(options.timeout>0);log.push('native_document');},getByRole(role,options){assert.equal(role,'region');assert.equal(options.name,'Conversa com a Aegis');return {async waitFor(){log.push('app_document');}};},async reload(options){assert.equal(options.waitUntil,'domcontentloaded');log.push('reload');if(reloadFailure)throw reloadFailure;}};}
test('installed WebView waits for the native app document before and after reload',async()=>{
  const log=[],page=pageFixture(log);assert.equal(await reloadReadyWindowsPage(async()=>page,()=>true),page);
  assert.deepEqual(log,['native_document','app_document','reload','app_document']);
});
test('an initially published CDP target cannot reload until its native document is ready',async()=>{
  const log=[],page=pageFixture(log);let finishNativeDocument;
  page.waitForFunction=()=>new Promise(resolve=>{finishNativeDocument=resolve;});
  const ready=reloadReadyWindowsPage(async()=>page,()=>true);
  await new Promise(resolve=>setImmediate(resolve));assert.deepEqual(log,[]);
  finishNativeDocument();assert.equal(await ready,page);
  assert.deepEqual(log,['app_document','reload','app_document']);
});
test('initial aborted navigation reacquires the target and retains every readiness assertion',async()=>{
  const log=[],first=pageFixture(log,Error('page.reload: net::ERR_ABORTED; maybe frame was detached?')),second=pageFixture(log);let calls=0;
  assert.equal(await reloadReadyWindowsPage(async()=>++calls===1?first:second,()=>true),second);assert.equal(calls,2);
  assert.deepEqual(log,['native_document','app_document','reload','native_document','app_document','reload','app_document']);
});
test('ordinary failures are not retried and detached retries are strictly bounded',async()=>{
  const failure=Error('ordinary navigation failure');let calls=0;
  await assert.rejects(reloadReadyWindowsPage(async()=>{calls++;return pageFixture([],failure);},()=>true),error=>error===failure);assert.equal(calls,1);
  calls=0;await assert.rejects(reloadReadyWindowsPage(async()=>{calls++;return pageFixture([],Error('Frame was detached'));},()=>true),/Frame was detached/);assert.equal(calls,3);
});
test('a navigation timeout remains a failure instead of being retried generically',async()=>{
  const failure=Error('page.reload: Timeout 30000ms exceeded.');let calls=0;
  await assert.rejects(reloadReadyWindowsPage(async()=>{calls++;return pageFixture([],failure);},()=>true),error=>error===failure);
  assert.equal(calls,1);
});
test('process exit and the shared deadline stop readiness without a retry loop',async()=>{
  let calls=0;await assert.rejects(reloadReadyWindowsPage(async()=>{calls++;},()=>false),/runtime exited/);assert.equal(calls,0);
  let time=0;await assert.rejects(reloadReadyWindowsPage(async()=>{calls++;time=46000;return pageFixture([]);},()=>true,{now:()=>time}),/readiness timed out/);assert.equal(calls,1);
});
