// Local pixel retouches of the owner's approved PixelLab/Pixelorama export.
// No model, pose generation, body transforms or dependency on earlier wave art.
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { inflateRawSync } from 'node:zlib';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { writePng } from './png.mjs';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
export const sourcePath = resolve(root, 'docs/ruri-pixellab/source/ruri-waving.png.pxo');
export const sequence = [0,1,2,3,4,5,6,7,6,7,8,9,0];
export const durations = sequence.map(() => 167); // 6 fps rounded to integer milliseconds.
export function readPxo(path = sourcePath) {
  const zip = readFileSync(path), entries = new Map();
  let end = zip.length - 22;
  while (end >= 0 && zip.readUInt32LE(end) !== 0x06054b50) end--;
  assert.ok(end >= 0, 'Missing ZIP central directory');
  let at = zip.readUInt32LE(end + 16);
  for (let i = 0; i < zip.readUInt16LE(end + 10); i++) {
    assert.equal(zip.readUInt32LE(at), 0x02014b50);
    const method = zip.readUInt16LE(at + 10), size = zip.readUInt32LE(at + 20);
    const nameLength = zip.readUInt16LE(at + 28), extra = zip.readUInt16LE(at + 30), comment = zip.readUInt16LE(at + 32);
    const name = zip.toString('utf8', at + 46, at + 46 + nameLength), local = zip.readUInt32LE(at + 42);
    const start = local + 30 + zip.readUInt16LE(local + 26) + zip.readUInt16LE(local + 28);
    assert.ok(method === 0 || method === 8, 'Unsupported ZIP method');
    const data = zip.subarray(start, start + size);
    entries.set(name, method === 8 ? inflateRawSync(data) : Buffer.from(data));
    at += 46 + nameLength + extra + comment;
  }
  const metadata = JSON.parse(entries.get('data.json').toString('utf8'));
  const frames = metadata.frames.map((_, i) => {
    const data = entries.get(`image_data/final_images/${i + 1}`);
    assert.equal(data.length, metadata.size_x * metadata.size_y * 4);
    return { width: metadata.size_x, height: metadata.size_y, data };
  });
  return { frames, fps: metadata.fps, durations: metadata.frames.map(f => f.duration * 1000 / metadata.fps) };
}
const inside = (x, y, polygon) => {
  let hit = false;
  for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
    const [xi, yi] = polygon[i], [xj, yj] = polygon[j];
    if ((yi > y) !== (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) hit = !hit;
  }
  return hit;
};
const offset = (x,y) => (y * 192 + x) * 4;
const eyeRegions = [
  null,null,null,
  [[[79,97],[85,96],[92,98],[97,103],[97,109],[89,111],[82,109],[79,103]], [[104,96],[109,93],[116,93],[116,101],[114,106],[109,108],[104,106],[103,101]]],
  [[[80,95],[90,95],[96,99],[97,105],[93,110],[85,110],[80,105]], [[106,98],[111,95],[117,95],[117,104],[113,110],[107,110],[104,107],[104,102]]],
  [[[80,95],[90,95],[96,99],[97,105],[93,110],[85,110],[80,105]], [[106,98],[111,95],[117,95],[117,104],[113,110],[107,110],[104,107],[104,102]]],
  [[[82,93],[90,93],[96,98],[97,105],[94,110],[87,110],[82,107],[80,101],[80,97],[81,95]], [[108,98],[112,96],[118,96],[118,105],[114,110],[107,110],[104,106],[104,102]]],
  [[[84,93],[91,93],[96,98],[98,102],[98,109],[88,110],[83,106],[81,102],[81,99],[82,97],[83,95]], [[109,101],[114,98],[119,98],[119,107],[114,112],[109,112],[106,108],[106,104]]],
  [[[81,95],[90,95],[96,99],[98,103],[97,108],[91,110],[85,109],[81,105]], [[108,100],[113,97],[119,97],[119,106],[114,110],[107,110],[105,107],[105,102]]],
  [[[79,95],[87,95],[94,97],[97,101],[97,108],[91,110],[84,110],[80,106]], [[107,97],[110,95],[117,95],[117,104],[113,110],[106,110],[104,106],[104,100]]]
];
// Frame 0 supplies both almond contours, smaller blue irises and glints.
// Placement follows each frame's original eye centers and head inclination.
const donorRegions = [
  { box:[78,102,17,10], polygon:[[80,102],[89,102],[92,105],[95,109],[95,112],[85,112],[81,109],[78,105]] },
  { box:[104,92,12,16], polygon:[[111,92],[116,92],[116,101],[112,105],[104,108],[104,99],[107,95]] }
];
const eyeTargets = [null,null,null,[[81,99,16,10],[104,95,12,13]],[[81,97,16,10],[105,97,12,12]],[[81,97,16,10],[105,97,12,12]],[[82,97,16,10],[106,98,12,12]],[[83,98,16,10],[108,100,12,12]],[[82,98,16,10],[107,99,12,12]],[[81,98,16,10],[105,97,12,12]]];
// Hand paths use restrained slate highlights on the existing dark glove. No
// sleeve/body repaint: cuff edge, thumb crease, palm plane and two finger tips.
const hands = {
  3: { box:[131,141,13,14], lines:[['edge',[[133,143],[136,142],[138,144]]],['palm',[[134,144],[133,147],[134,149]]],['light',[[134,144],[135,144]]],['crease',[[138,147],[139,149],[139,151]]],['edge',[[137,152],[139,153],[141,151]]]] },
  4: { box:[127,113,11,12], lines:[['edge',[[129,115],[130,114],[131,114]]],['edge',[[133,114],[134,115],[135,118]]],['palm',[[130,116],[130,120],[132,122]]],['light',[[129,116],[129,119]]],['crease',[[132,115],[132,118]]],['crease',[[129,119],[131,120]]],['edge',[[134,121],[133,123],[131,123]]]] },
  5: { box:[127,112,11,12], lines:[['edge',[[129,114],[130,113],[131,113]]],['edge',[[133,114],[134,115],[135,118]]],['palm',[[130,115],[130,119],[132,121]]],['light',[[129,115],[129,118]]],['crease',[[132,114],[132,117]]],['crease',[[129,118],[131,119]]],['edge',[[134,120],[133,122],[131,122]]]] },
  6: { box:[139,116,13,13], lines:[['edge',[[141,119],[144,117],[146,117]]],['palm',[[141,120],[141,123],[143,124]]],['light',[[142,120],[143,119]]],['crease',[[146,119],[148,120]]],['edge',[[148,122],[149,123]]],['crease',[[144,123],[145,125]]],['edge',[[142,126],[144,126]]]] },
  7: { box:[139,139,11,12], lines:[['edge',[[140,140],[143,141],[145,142]]],['palm',[[140,142],[142,144],[142,146]]],['light',[[141,142],[142,143]]],['crease',[[144,143],[145,145]]],['edge',[[147,145],[147,147],[145,148]]],['crease',[[143,147],[143,149]]]] },
  8: { box:[124,153,9,12], lines:[['edge',[[125,155],[127,154],[129,154]]],['palm',[[125,157],[126,160],[128,161]]],['light',[[126,156],[126,158]]],['crease',[[129,157],[129,160]]],['edge',[[128,163],[130,162]]]] }
};
const colors = { edge:[53,58,73,255], palm:[43,47,60,255], light:[65,71,89,255], crease:[17,19,28,255] };
function line(frame, points, color) {
  for (let i = 1; i < points.length; i++) {
    const [ax,ay]=points[i-1], [bx,by]=points[i], n=Math.max(Math.abs(bx-ax),Math.abs(by-ay));
    for(let t=0;t<=n;t++) {
      const x=Math.round(ax+(bx-ax)*t/n),y=Math.round(ay+(by-ay)*t/n),o=offset(x,y);
      // Preserve original glove coverage; highlights never extend the hand.
      if(frame.data[o+3] && Math.max(...frame.data.subarray(o,o+3)) < 100) frame.data.set(color,o);
    }
  }
}
function retouchEyes(frame, source, reference, index) {
  const regions = eyeRegions[index];
  for(const region of regions) {
    for(let y=92;y<113;y++)for(let x=78;x<120;x++) {
      if(!inside(x+.5,y+.5,region))continue;
      const candidates=[];
      for(let sy=Math.max(88,y-9);sy<=Math.min(116,y+9);sy++)for(let sx=Math.max(77,x-9);sx<=Math.min(119,x+9);sx++) {
        if(regions.some(p=>inside(sx+.5,sy+.5,p)))continue;
        const o=offset(sx,sy),rgb=source.data.subarray(o,o+3);
        if(Math.min(...rgb)<190 || Math.max(...rgb)-Math.min(...rgb)>45)continue;
        candidates.push({d:(sx-x)**2+(sy-y)**2,rgb});
      }
      candidates.sort((a,b)=>a.d-b.d);
      const nearby=candidates.slice(0,6), weights=nearby.map(p=>1/p.d),total=weights.reduce((a,b)=>a+b,0);
      assert.ok(nearby.length);
      for(let c=0;c<3;c++)frame.data[offset(x,y)+c]=Math.round(nearby.reduce((sum,p,i)=>sum+p.rgb[c]*weights[i],0)/total);
    }
  }
  donorRegions.forEach((donor,eye)=>{
    const [dx,dy,dw,dh]=donor.box,[tx,ty,tw,th]=eyeTargets[index][eye];
    for(let y=0;y<th;y++)for(let x=0;x<tw;x++) {
      const sx=dx+Math.floor(x*dw/tw),sy=dy+Math.floor(y*dh/th);
      if(!inside(sx+.5,sy+.5,donor.polygon))continue;
      const o=offset(sx,sy),dst=offset(tx+x,ty+y);
      reference.data.copy(frame.data,dst,o,o+4);
    }
  });
}
export function buildRefinedWave(source = readPxo()) {
  assert.equal(source.frames.length,10);assert.equal(source.fps,6);
  const frames=source.frames.map(f=>({...f,data:Buffer.from(f.data)}));
  for(let i=3;i<10;i++){
    retouchEyes(frames[i],source.frames[i],source.frames[0],i);
    for(const [color,points]of hands[i]?.lines??[])line(frames[i],points,colors[color]);
    // Trim one outer column on the returning glove; the cuff stays attached.
    if(i===8)for(let y=155;y<=164;y++)frames[i].data.fill(0,offset(132,y),offset(132,y)+4);
  }
  const atlas={width:1920,height:208,data:Buffer.alloc(1920*208*4)};
  frames.forEach((f,i)=>{for(let y=0;y<208;y++)f.data.copy(atlas.data,(y*1920+i*192)*4,y*192*4,(y+1)*192*4)});
  return {frames,atlas};
}
if(process.argv[1] && resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  const {frames,atlas}=buildRefinedWave();
  const output=resolve(process.argv[2]??'/tmp/ruri-pixellab-review/final');
  mkdirSync(output,{recursive:true});
  writePng(resolve(output,'ruri-waving-pixellab.png'),atlas);
  frames.forEach((frame,i)=>writePng(resolve(output,`frame-${i}.png`),frame));
  writeFileSync(resolve(output,'sequence.json'),JSON.stringify({sequence,durations,waveMs:durations.reduce((a,b)=>a+b,0)},null,2)+'\n');
  console.log(output);
}
