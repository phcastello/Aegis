import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { readPxo, buildRefinedWave, sequence, durations } from '../../../scripts/ruri/refine-pixellab.mjs';
import { cropPng, decodePng, encodePng } from '../../../scripts/ruri/png.mjs';

const root = new URL('../../../', import.meta.url);
const source = readPxo();
const bytes = readFileSync(new URL('../src/assets/ruri-waving-pixellab.png', import.meta.url));
const atlas = decodePng(bytes);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const hands = { 3:[131,141,13,14],4:[127,113,11,12],5:[127,112,11,12],6:[139,116,13,13],7:[139,139,11,12],8:[124,153,9,12] };
const within = (x,y,[left,top,width,height]) => x>=left && y>=top && x<left+width && y<top+height;

// These are source/scope regressions, not an automated artistic approval.
test('the owner exports are immutable and the runtime atlas reproduces their local retouches', () => {
  for (const [file, expected] of [
    ['ruri-waving.png.pxo','6fd967715e02aa3699406481f9c9b73d19ee196f5a76d41c848b5f6b2e400ed1'],
    ['ruri-waving.png.gif','6aa2f9d398af0e31ba740a40c7e47e25aa74bf134bebe8fe0f205576d50bd6a8']
  ]) assert.equal(hash(readFileSync(new URL(`docs/ruri-pixellab/source/${file}`,root))),expected);
  assert.equal(source.frames.length,10);
  assert.equal(source.fps,6);
  assert.deepEqual([atlas.width,atlas.height],[1920,208]);
  assert.deepEqual(bytes,encodePng(buildRefinedWave(source).atlas));
});

test('frames 0–2 are intact and 3–9 change only eyes and the waving glove', () => {
  source.frames.forEach((original,index) => {
    const final = cropPng(atlas,index*192,0,192,208);
    if(index<3) { assert.deepEqual(final.data,original.data); return; }
    let eyes=0,hand=0;
    for(let y=0;y<208;y++)for(let x=0;x<192;x++) {
      const p=(y*192+x)*4;
      if(final.data.subarray(p,p+4).equals(original.data.subarray(p,p+4)))continue;
      const eye=(within(x,y,[78,93,21,20]) || within(x,y,[103,93,17,20]));
      const glove=hands[index] && within(x,y,hands[index]);
      assert.ok(eye || glove,`frame ${index}: unintended change at ${x},${y}`);
      if(eye)eyes++;if(glove)hand++;
      if(!(index===8 && x===132 && y>=155 && y<=164)) assert.equal(final.data[p+3],original.data[p+3],'no added silhouette or body motion');
    }
    assert.ok(eyes>0,`eyes corrected in frame ${index}`);
    if(index<=8)assert.ok(hand>0,`glove corrected in frame ${index}`);
    assert.deepEqual(cropPng(final,0,170,192,38).data,cropPng(original,0,170,192,38).data,'feet and lower cloak unchanged');
    assert.deepEqual(cropPng(final,98,110,6,9).data,cropPng(original,98,110,6,9).data,'mouth/expression unchanged');
  });
});

test('the supplied neutral pose joins canonical idle with identical coverage and negligible color difference', () => {
  const canonical=decodePng(readFileSync(new URL('../src/assets/ruri-spritesheet.png',import.meta.url)));
  assert.equal(hash(readFileSync(new URL('../src/assets/ruri-spritesheet.png',import.meta.url))),'05c42c26f59ea4a7fae2f8fcdb42d7a3a99087bb052de0407c38f944d6bc2da1');
  const idle=cropPng(canonical,0,0,192,208),neutral=source.frames[0];
  let changed=0;
  for(let p=0;p<idle.data.length;p+=4){
    assert.equal(neutral.data[p+3],idle.data[p+3]);
    if(!neutral.data.subarray(p,p+4).equals(idle.data.subarray(p,p+4)))changed++;
  }
  assert.equal(changed,61,'preserve the supplied frame rather than replacing it with an earlier idle');
});

test('only the middle of the wave repeats, with one ascent and one descent at the source frame rate', () => {
  assert.deepEqual(sequence,[0,1,2,3,4,5,6,7,6,7,8,9,0]);
  assert.equal(durations.length,13);
  assert.ok(durations.every(ms=>Math.abs(ms-1000/source.fps)<0.5));
  assert.equal(durations.reduce((a,b)=>a+b,0),2171);
});
