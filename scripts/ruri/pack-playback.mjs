// Pack existing approved pixels into one texture. Runtime loops only change
// the crop, never the image URL or texture dimensions. No art is retouched here.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { cropPng, encodePng, readPng, writePng } from './png.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const assets = resolve(root, 'packages/aegis-client/src/assets');
export const playbackPath = resolve(assets, 'ruri-playback.png');

export function packPlayback() {
  const idle = cropPng(readPng(resolve(assets, 'ruri-spritesheet.png')), 0, 0, 1536, 208);
  const wave = readPng(resolve(assets, 'ruri-waving-pixellab.png'));
  assert.deepEqual([wave.width, wave.height], [1920, 208]);
  const image = { width: 1920, height: 416, data: Buffer.alloc(1920 * 416 * 4) };
  for (const [row, source] of [idle, wave].entries()) {
    for (let y = 0; y < 208; y++) {
      source.data.copy(image.data, ((row * 208 + y) * 1920) * 4,
        y * source.width * 4, (y + 1) * source.width * 4);
    }
  }
  return image;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const image = packPlayback();
  if (process.argv.includes('--check')) assert.deepEqual(readFileSync(playbackPath), encodePng(image));
  else writePng(playbackPath, image);
  console.log(`${process.argv.includes('--check') ? 'Verified' : 'Packed'} Ruri playback: 1920 × 416, unchanged idle/wave pixels`);
}
