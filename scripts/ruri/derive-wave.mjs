// Development-only, rasterization of individually drawn local arm poses. Never imported by the app.
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

// Each contour is authored separately in wave-spec.json, at native resolution.
// No donor-arm sampling, rotation, scaling or bone transform is used.
export function buildWave(atlas, spec) {
  const { width, height } = spec.cell;
  const canonical = cropPng(atlas, spec.base.column * width, spec.base.row * height, width, height);
  const body = { width, height, data: Buffer.from(canonical.data) };
  const gloveMask = new Uint8Array(width * height);
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    if (!pointInPolygon(x + 0.5, y + 0.5, spec.gloveFill.polygon)) continue;
    const sourceX = x + spec.gloveFill.sourceOffset[0], sourceY = y + spec.gloveFill.sourceOffset[1];
    if (sourceX < 0 || sourceX >= width || sourceY < 0 || sourceY >= height) throw Error('Glove fill exceeds canonical cell.');
    canonical.data.copy(body.data, (y * width + x) * 4, (sourceY * width + sourceX) * 4, (sourceY * width + sourceX) * 4 + 4);
    gloveMask[y * width + x] = 1;
  }
  const masks = [], frames = spec.poses.map((pose, index) => {
    const mask = new Uint8Array(width * height);
    const frame = { width, height, data: Buffer.from(index === 0 ? canonical.data : body.data) };
    if (index) mask.set(gloveMask);
    for (const layer of pose.layers) {
      const color = spec.palette[layer.color];
      if (!color || !layer.polygon || layer.polygon.length < 3) throw Error('Invalid drawn contour.');
      const [sourceX, sourceY] = color.source;
      const sourceIndex = (sourceY * width + sourceX) * 4;
      if (!color.rgba.every((value, channel) => canonical.data[sourceIndex + channel] === value)) throw Error('Palette differs from canonical eyedropper.');
      for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
        if (!pointInPolygon(x + .5, y + .5, layer.polygon)) continue;
        frame.data.set(color.rgba, (y * width + x) * 4);
        mask[y * width + x] = 1;
      }
    }
    masks.push(mask);
    return frame;
  });
  const image = { width: width * frames.length, height, data: Buffer.alloc(width * frames.length * height * 4) };
  for (let index = 0; index < frames.length; index++) for (let y = 0; y < height; y++) {
    frames[index].data.copy(image.data, (y * image.width + index * width) * 4, y * width * 4, (y + 1) * width * 4);
  }
  const unionMask = new Uint8Array(width * height);
  for (const mask of masks) mask.forEach((value, index) => { if (value) unionMask[index] = 1; });
  return { canonical, frames, image, allowedMasks: masks, unionMask };
}

export function inspectWave(result, atlas) {
  const { canonical, frames, allowedMasks: masks } = result, { width, height } = canonical;
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
