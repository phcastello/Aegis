import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ownedWindowInspectionScript, parseOwnedWindowState } from './windows-owned-window.mjs';
test('native window inspection filters exact owned product HWNDs, including hidden ones',()=>{
  const script=ownedWindowInspectionScript(12345);
  assert.match(script,/GetWindowThreadProcessId/);assert.match(script,/owner==processId/);assert.match(script,/"Aegis",StringComparison\.Ordinal/);
  assert.match(script,/Find\(\[uint32\]12345\)/);assert.doesNotMatch(script,/MainWindowHandle/);
  assert.doesNotMatch(script,/::PostMessage\(\$windows/);
  assert.match(ownedWindowInspectionScript(12345,'requestClose'),/::PostMessage\(\$windows\[0\],0x0010/);
  assert.throws(()=>ownedWindowInspectionScript(12345,'arbitrary command'),/Invalid owned Windows window operation\./);
  for(const value of [0,-1,1.5,0x100000000,'123; private command'])assert.throws(()=>ownedWindowInspectionScript(value),/Invalid owned Windows process identifier\./);
});
test('real product visibility is retained without unrelated window metadata',()=>{
  for(const visible of [true,false])assert.deepEqual(parseOwnedWindowState(JSON.stringify({windowCount:1,visible,mainWindowTitle:'Aegis',otherWindow:'private title'})),{windowCount:1,visible,mainWindowTitle:'Aegis'});
  for(const windowCount of [0,2])assert.deepEqual(parseOwnedWindowState(JSON.stringify({windowCount,visible:true,mainWindowTitle:'technical IPC trap'})),{windowCount});
});
test('invalid or substitute HWND observations fail without exposing source data',()=>{
  for(const input of ['private text',JSON.stringify({windowCount:1,visible:true,mainWindowTitle:'com.aegis.node-siw'}),JSON.stringify({windowCount:1,visible:'false',mainWindowTitle:'Aegis'})])
    assert.throws(()=>parseOwnedWindowState(input),error=>error.message==='Invalid owned Windows window observation.');
});
