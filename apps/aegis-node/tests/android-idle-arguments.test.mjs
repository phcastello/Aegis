import assert from 'node:assert/strict';
import test from 'node:test';
import {idleInstrumentationArguments} from '../scripts/android-idle-arguments.mjs';

function extras(argv) {
  const output={};
  for(let index=0;index<argv.length;index++)if(argv[index]==='-e') {
    const name=argv[++index];const value=argv[++index];
    assert(name&&value&&!value.startsWith('-'),'Each am extra needs a nonempty value after adb shell joins argv');
    output[name]=value;
  }
  return output;
}
test('before idle passes no empty identity extra through the adb remote shell',()=>{
  const argv=idleInstrumentationArguments({mode:'before',pairingCode:'123456',expectedVersion:'0.7.0-unstable.11'});
  assert(argv.every(value=>value.length>0));
  // ADB joins argv with spaces; this synthetic command contains only safe words.
  assert.deepEqual(argv.join(' ').split(/\s+/),argv);
  const values=extras(argv);
  assert.equal(values.pairingCode,'123456');assert.equal(values.expectedVersion,'0.7.0-unstable.11');
  assert(!Object.hasOwn(values,'expectedNodeId'));
});
test('after idle requires the preserved identity and omits the unused bootstrap extra',()=>{
  const nodeId='01234567-89ab-4def-8123-456789abcdef';
  const argv=idleInstrumentationArguments({mode:'after',expectedNodeId:nodeId,expectedVersion:'0.7.0-unstable.12'});
  const values=extras(argv);assert.equal(values.expectedNodeId,nodeId);assert.equal(values.idleMode,'after');
  assert(!Object.hasOwn(values,'pairingCode'));assert(argv.every(value=>value.length>0));
  assert.throws(()=>idleInstrumentationArguments({mode:'after',expectedVersion:'0.7.0-unstable.12'}));
});
