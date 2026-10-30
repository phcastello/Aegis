import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { deflateSync } from 'node:zlib';
import { cropPng, decodePng, encodePng } from '../../../scripts/ruri/png.mjs';

const canonical = new URL('../src/assets/ruri-spritesheet.png', import.meta.url);

test('the canonical Ruri reference stays immutable and decodes at its original dimensions', () => {
  const bytes = readFileSync(canonical);
  assert.equal(createHash('sha256').update(bytes).digest('hex'), '05c42c26f59ea4a7fae2f8fcdb42d7a3a99087bb052de0407c38f944d6bc2da1');
  const image = decodePng(bytes);
  assert.equal(image.width, 1536); assert.equal(image.height, 2288);
  const neutral = cropPng(image, 0, 0, 192, 208);
  assert.deepEqual(decodePng(encodePng(neutral)), neutral, 'translucent canonical outlines survive a lossless roundtrip');
});

test('RGBA roundtrip preserves low alpha and hidden RGB without color conversion', () => {
  const image = { width: 3, height: 2, data: Buffer.from([
    11, 22, 33, 0, 219, 87, 52, 1, 193, 164, 137, 64,
    40, 80, 120, 127, 255, 128, 64, 254, 5, 10, 20, 255
  ]) };
  assert.deepEqual(decodePng(encodePng(image)), image);
  assert.deepEqual(cropPng(image, 1, 0, 2, 2).data, Buffer.from([
    219, 87, 52, 1, 193, 164, 137, 64, 255, 128, 64, 254, 5, 10, 20, 255
  ]));
  assert.throws(() => cropPng(image, 2, 0, 2, 2), /outside/);
});

// Two identical RGBA pixels on each row; the expected filtered bytes below are
// independent known vectors, including first-pixel/first-row predictor edges.
test('the decoder supports all five PNG scanline filters and rejects corrupted CRCs', () => {
  const image = { width: 2, height: 2, data: Buffer.from([
    10, 20, 30, 40, 50, 60, 70, 80,
    20, 30, 40, 50, 60, 70, 80, 90
  ]) };
  const first = [0, 10, 20, 30, 40, 50, 60, 70, 80];
  const rows = [
    [0, 20, 30, 40, 50, 60, 70, 80, 90],
    [1, 20, 30, 40, 50, 40, 40, 40, 40],
    [2, 10, 10, 10, 10, 10, 10, 10, 10],
    [3, 15, 20, 25, 30, 25, 25, 25, 25],
    [4, 10, 10, 10, 10, 10, 10, 10, 10]
  ];
  const original = encodePng(image);
  for (const row of rows) {
    const compressed = deflateSync(Buffer.from([...first, ...row]));
    const idat = Buffer.alloc(compressed.length + 12);
    idat.writeUInt32BE(compressed.length); idat.write('IDAT', 4); compressed.copy(idat, 8);
    // PNG's standard polynomial, evaluated bitwise to test the table-based CRC.
    let crc = 0xffffffff;
    for (const byte of idat.subarray(4, -4)) {
      crc ^= byte;
      for (let bit = 0; bit < 8; bit++) crc = crc & 1 ? 0xedb88320 ^ (crc >>> 1) : crc >>> 1;
    }
    idat.writeUInt32BE((crc ^ 0xffffffff) >>> 0, idat.length - 4);
    const png = Buffer.concat([original.subarray(0, 33), idat, original.subarray(-12)]);
    assert.deepEqual(decodePng(png), image, `filter ${row[0]} reconstructs exact bytes`);
    png[41] ^= 1;
    assert.throws(() => decodePng(png), /CRC mismatch/);
  }
});
