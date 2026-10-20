import assert from 'node:assert/strict';
import { test } from 'node:test';
import { parsePendingToastHistory } from './windows-toast-history.mjs';
const card={count:1,expirationTicks:'639013200000000000',genericTextMatches:true,silent:true,opensAppOnly:true,hasNoActions:true};
test('pending toast observation keeps only fixed, generic status metadata',()=>{
  assert.deepEqual(parsePendingToastHistory(JSON.stringify({...card,xml:'private payload',token:'never-return'})),card);
});
test('absent pending card is distinct from a valid real card',()=>{
  assert.deepEqual(parsePendingToastHistory('{"count":0,"xml":"never-return"}'),{count:0});
});
test('invalid or nongeneric Windows observations fail without disclosing their source',()=>{
  for(const value of ['secret not-json',JSON.stringify({...card,count:2}),JSON.stringify({...card,expirationTicks:null}),JSON.stringify({...card,silent:false}),JSON.stringify({...card,hasNoActions:false})])
    assert.throws(()=>parsePendingToastHistory(value),error=>error.message==='Invalid bounded Windows pending-notification observation.');
});
