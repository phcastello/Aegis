// Development-only, deterministic arm compositing. Never imported by the app.
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';
import { cropPng, encodePng, readPng, writePng } from './png.mjs';

export function pointInPolygon(x, y, polygon) {
  let inside = false;
  for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
    const [ax, ay] = polygon[i], [bx, by] = polygon[j];
    if ((ay > y) !== (by > y) && x < (bx - ax) * (y - ay) / (by - ay) + ax) inside = !inside;
  }
  return inside;
}

function sourceOver(target, targetIndex, source, sourceIndex) {
  const sourceAlpha = source[sourceIndex + 3] / 255;
  if (sourceAlpha === 0) return;
  const targetAlpha = target[targetIndex + 3] / 255;
  // Copy donor pixels exactly when no blend is necessary, including soft alpha.
  if (sourceAlpha === 1 || targetAlpha === 0) {
    source.copy(target, targetIndex, sourceIndex, sourceIndex + 4);
    return;
  }
  const alpha = sourceAlpha + targetAlpha * (1 - sourceAlpha);
  for (let channel = 0; channel < 3; channel++) {
    target[targetIndex + channel] = Math.round((source[sourceIndex + channel] * sourceAlpha
      + target[targetIndex + channel] * targetAlpha * (1 - sourceAlpha)) / alpha);
  }
  target[targetIndex + 3] = Math.round(alpha * 255);
}

export function armSourcePoint(x, y, angle, spec) {
  const radians = angle * Math.PI / 180, cosine = Math.cos(radians), sine = Math.sin(radians);
  const dx = x + 0.5 - spec.pivot[0], dy = y + 0.5 - spec.pivot[1];
  return [(cosine * dx + sine * dy) / spec.localArmScale + spec.pivot[0] - spec.offset[0],
    (-sine * dx + cosine * dy) / spec.localArmScale + spec.pivot[1] - spec.offset[1]];
}

export function buildWave(atlas, spec) {
  const { width, height } = spec.cell;
  const canonical = cropPng(atlas, spec.base.column * width, spec.base.row * height, width, height);
  const donor = cropPng(atlas, spec.donor.column * width, spec.donor.row * height, width, height);
  const body = { width, height, data: Buffer.from(canonical.data) };
  const gloveMask = new Uint8Array(width * height);
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    if (!pointInPolygon(x + 0.5, y + 0.5, spec.gloveFill.polygon)) continue;
    const sourceX = x + spec.gloveFill.sourceOffset[0], sourceY = y + spec.gloveFill.sourceOffset[1];
    if (sourceX < 0 || sourceX >= width || sourceY < 0 || sourceY >= height) throw Error('Glove fill exceeds canonical cell.');
    const targetIndex = (y * width + x) * 4, sourceIndex = (sourceY * width + sourceX) * 4;
    canonical.data.copy(body.data, targetIndex, sourceIndex, sourceIndex + 4);
    gloveMask[y * width + x] = 1;
  }
  const masks = [], frames = spec.angles.map(angle => {
    const mask = new Uint8Array(width * height);
    if (angle === null) {
      masks.push(mask);
      return { width, height, data: Buffer.from(canonical.data) };
    }
    const frame = { width, height, data: Buffer.from(body.data) };
    mask.set(gloveMask);
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
      const [sourceX, sourceY] = armSourcePoint(x, y, angle, spec);
      if (sourceX < 0 || sourceX >= width || sourceY < 0 || sourceY >= height
        || !pointInPolygon(sourceX, sourceY, spec.armPolygon)) continue;
      const sourceIndex = (Math.floor(sourceY) * width + Math.floor(sourceX)) * 4;
      // Polygon membership is independent of final differences. A transparent
      // source does not erase the canonical cape and cannot punch holes in it.
      mask[y * width + x] = 1;
      sourceOver(frame.data, (y * width + x) * 4, donor.data, sourceIndex);
    }
    masks.push(mask);
    return frame;
  });
  const image = { width: width * frames.length, height, data: Buffer.alloc(width * frames.length * height * 4) };
  frames.forEach((frame, index) => {
    for (let y = 0; y < height; y++) {
      frame.data.copy(image.data, (y * image.width + index * width) * 4, y * width * 4, (y + 1) * width * 4);
    }
  });
  const unionMask = new Uint8Array(width * height);
  masks.forEach(mask => mask.forEach((value, index) => { if (value) unionMask[index] = 1; }));
  return { image, frames, canonical, masks, allowedMasks: masks, unionMask };
}

export function inspectWave(result, atlas) {
  const { canonical, frames, masks } = result, { width, height } = canonical;
  const palette = new Set();
  for (let index = 0; index < atlas.data.length; index += 4) palette.add(atlas.data.readUInt32BE(index));
  const bbox = data => {
    const box = [width, height, -1, -1];
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
      if (!data[(y * width + x) * 4 + 3]) continue;
      box[0] = Math.min(box[0], x); box[1] = Math.min(box[1], y);
      box[2] = Math.max(box[2], x); box[3] = Math.max(box[3], y);
    }
    return box;
  };
  const frameReports = frames.map((frame, frameIndex) => {
    let changedPixels = 0, changedOutsideMask = 0, alphaHoles = 0;
    const changedRuns = [], novelColors = new Map();
    for (let y = 0; y < height; y++) {
      let runStart = null;
      for (let x = 0; x < width; x++) {
        const pixel = y * width + x, index = pixel * 4;
        const color = frame.data.readUInt32BE(index), original = canonical.data.readUInt32BE(index);
        const different = color !== original;
        if (different) {
          changedPixels++;
          if (!masks[frameIndex][pixel]) changedOutsideMask++;
          if (frame.data[index + 3] < canonical.data[index + 3]) alphaHoles++;
          if (!palette.has(color)) novelColors.set(color, (novelColors.get(color) ?? 0) + 1);
          if (runStart === null) runStart = x;
        }
        if ((!different || x === width - 1) && runStart !== null) {
          changedRuns.push({ y, x0: runStart, x1: different ? x : x - 1 }); runStart = null;
        }
      }
    }
    return { frame: frameIndex, bbox: bbox(frame.data), changedPixels, changedOutsideMask, alphaHoles,
      novelColors: [...novelColors].map(([color, pixels]) => ({ rgba: color.toString(16).padStart(8, '0'), pixels })), changedRuns };
  });
  return { canonicalBbox: bbox(canonical.data), frames: frameReports };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const { values } = parseArgs({ options: { check: { type: 'boolean', default: false },
    output: { type: 'string' }, report: { type: 'string' }, mask: { type: 'string' } } });
  const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
  const output = resolve(values.output ?? resolve(root, 'packages/aegis-client/src/assets/ruri-waving-consistent.png'));
  const spec = JSON.parse(readFileSync(resolve(root, 'scripts/ruri/wave-spec.json'), 'utf8'));
  const source = resolve(root, spec.canonicalAtlas);
  if (output === source) throw Error('Refusing to overwrite the canonical atlas.');
  if (values.check && (values.mask || values.report)) throw Error('--check writes no artifacts; omit --mask and --report.');
  const bytes = readFileSync(source), sha256 = createHash('sha256').update(bytes).digest('hex');
  if (sha256 !== spec.canonicalAtlasSha256) throw Error('Canonical atlas hash differs from the reviewed reference.');
  const atlas = readPng(source), result = buildWave(atlas, spec);
  if (values.check) {
    if (!readFileSync(output).equals(encodePng(result.image))) throw Error('Derived asset differs from the reviewed arm recipe.');
    console.log(`Verified ${result.frames.length} reproducible arm poses: ${output}`);
    process.exit(0);
  }
  writePng(output, result.image);
  if (values.mask) {
    const mask = { width: result.canonical.width, height: result.canonical.height,
      data: Buffer.alloc(result.unionMask.length * 4) };
    result.unionMask.forEach((value, index) => { if (value) mask.data.fill(255, index * 4, index * 4 + 4); });
    writePng(values.mask, mask);
  }
  if (values.report) writeFileSync(values.report, JSON.stringify(inspectWave(result, atlas), null, 2) + '\n');
  console.log(`Derived ${result.frames.length} arm poses: ${output}`);
}
