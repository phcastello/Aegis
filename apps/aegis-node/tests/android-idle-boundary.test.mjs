import assert from 'node:assert/strict';
import test from 'node:test';
import {captureIdleBoundary, idleSnapshotAckPath} from '../scripts/android-idle-boundary.mjs';

const nonce = '01234567-89ab-cdef-0123-456789abcdef';
const event = {event:'end',phase:'background',snapshotNonce:nonce};
const complete = {metrics:{http_requests:1},cpu:{available:true,processes:[{kind:'app'}]}};

test('the final process may finish only after the host has captured its CPU and traffic', async () => {
  const steps=[];
  let finishSnapshot;
  const result=captureIdleBoundary(event, async () => {
    steps.push('snapshot-start');
    await new Promise(resolve=>{finishSnapshot=resolve;});
    steps.push('snapshot-complete');
    return complete;
  }, async marker => {
    assert.equal(marker,`/data/user/0/com.aegis.node/cache/aegis-idle-snapshot-${nonce}.ack`);
    steps.push('acknowledge');
  });
  assert.deepEqual(steps,['snapshot-start']);
  finishSnapshot();
  assert.equal(await result,complete);
  assert.deepEqual(steps,['snapshot-start','snapshot-complete','acknowledge']);
});

test('an incomplete snapshot or untrusted cache path is never acknowledged', async () => {
  let acknowledged=false;
  await assert.rejects(captureIdleBoundary(event,async()=>({...complete,cpu:{available:false,processes:[]}}),()=>{acknowledged=true;}));
  await assert.rejects(captureIdleBoundary(event,async()=>({...complete,metrics:null}),()=>{acknowledged=true;}));
  assert.equal(acknowledged,false);
  assert.throws(()=>idleSnapshotAckPath('../../private'));
  await assert.rejects(captureIdleBoundary({...event,phase:'foreground_closed'},async()=>complete,()=>{acknowledged=true;}));
  assert.equal(acknowledged,false);
});

test('ordinary boundaries require no handshake and preserve their measurement', async () => {
  const output=await captureIdleBoundary({event:'start',phase:'foreground_closed'},async()=>complete,()=>assert.fail('No marker was requested'));
  assert.equal(output,complete);
});
