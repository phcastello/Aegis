// Development-only PNG helpers. Keep RGBA bytes intact, including translucent
// outlines that a canvas premultiply/unpremultiply roundtrip can change.
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync } from 'node:fs';
import { deflateSync, inflateSync } from 'node:zlib';

const signature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
const crcTable = Uint32Array.from({ length: 256 }, (_, byte) => {
  let crc = byte;
  for (let bit = 0; bit < 8; bit++) crc = crc & 1 ? 0xedb88320 ^ (crc >>> 1) : crc >>> 1;
  return crc >>> 0;
});

function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) crc = crcTable[(crc ^ byte) & 255] ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}

function chunk(type, data = Buffer.alloc(0)) {
  const output = Buffer.alloc(data.length + 12);
  output.writeUInt32BE(data.length, 0);
  output.write(type, 4, 4, 'ascii');
  data.copy(output, 8);
  output.writeUInt32BE(crc32(output.subarray(4, -4)), output.length - 4);
  return output;
}

function paeth(left, above, upperLeft) {
  const prediction = left + above - upperLeft;
  const a = Math.abs(prediction - left), b = Math.abs(prediction - above), c = Math.abs(prediction - upperLeft);
  return a <= b && a <= c ? left : b <= c ? above : upperLeft;
}

export function decodePng(input) {
  const bytes = Buffer.from(input);
  assert.deepEqual(bytes.subarray(0, 8), signature, 'Expected a PNG signature');
  const imageData = [];
  let width, height, ended = false;
  for (let offset = 8; offset < bytes.length;) {
    assert.ok(offset + 12 <= bytes.length, 'Truncated PNG chunk');
    const size = bytes.readUInt32BE(offset), end = offset + 12 + size;
    assert.ok(end <= bytes.length, 'Truncated PNG chunk data');
    const type = bytes.toString('ascii', offset + 4, offset + 8);
    const data = bytes.subarray(offset + 8, end - 4);
    assert.equal(bytes.readUInt32BE(end - 4), crc32(bytes.subarray(offset + 4, end - 4)), `PNG ${type} CRC mismatch`);
    if (type === 'IHDR') {
      assert.equal(width, undefined, 'Duplicate PNG header');
      assert.equal(size, 13, 'Invalid PNG header');
      width = data.readUInt32BE(0); height = data.readUInt32BE(4);
      assert.ok(width > 0 && height > 0, 'PNG dimensions must be positive');
      assert.deepEqual([...data.subarray(8)], [8, 6, 0, 0, 0], 'Expected non-interlaced RGBA8 PNG');
    } else if (type === 'IDAT') imageData.push(data);
    else if (type === 'IEND') { ended = true; break; }
    offset = end;
  }
  assert.ok(width && height && ended && imageData.length, 'PNG is missing required chunks');
  const stride = width * 4, scanlines = inflateSync(Buffer.concat(imageData));
  assert.equal(scanlines.length, (stride + 1) * height, 'Unexpected PNG scanline size');
  const data = Buffer.alloc(width * height * 4);
  for (let y = 0; y < height; y++) {
    const row = y * (stride + 1), filter = scanlines[row];
    assert.ok(filter <= 4, `Unsupported PNG filter ${filter}`);
    for (let x = 0; x < stride; x++) {
      const destination = y * stride + x;
      const left = x >= 4 ? data[destination - 4] : 0;
      const above = y ? data[destination - stride] : 0;
      const upperLeft = y && x >= 4 ? data[destination - stride - 4] : 0;
      const prediction = filter === 0 ? 0 : filter === 1 ? left : filter === 2 ? above
        : filter === 3 ? Math.floor((left + above) / 2) : paeth(left, above, upperLeft);
      data[destination] = (scanlines[row + 1 + x] + prediction) & 255;
    }
  }
  return { width, height, data };
}

export function encodePng({ width, height, data }) {
  assert.ok(Number.isInteger(width) && width > 0 && Number.isInteger(height) && height > 0, 'Invalid PNG dimensions');
  assert.equal(data.length, width * height * 4, 'Expected one RGBA tuple per pixel');
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0); header.writeUInt32BE(height, 4);
  header[8] = 8; header[9] = 6;
  const stride = width * 4, scanlines = Buffer.alloc((stride + 1) * height);
  for (let y = 0; y < height; y++) scanlines.set(data.subarray(y * stride, (y + 1) * stride), y * (stride + 1) + 1);
  return Buffer.concat([signature, chunk('IHDR', header), chunk('IDAT', deflateSync(scanlines, { level: 9 })), chunk('IEND')]);
}

export function readPng(path) { return decodePng(readFileSync(path)); }
export function writePng(path, image) { writeFileSync(path, encodePng(image)); }

export function cropPng(image, x, y, width, height) {
  assert.ok([x, y, width, height].every(Number.isInteger) && x >= 0 && y >= 0 && width > 0 && height > 0
    && x + width <= image.width && y + height <= image.height, 'PNG crop is outside the source image');
  const data = Buffer.alloc(width * height * 4);
  for (let row = 0; row < height; row++) {
    const start = ((y + row) * image.width + x) * 4;
    image.data.copy(data, row * width * 4, start, start + width * 4);
  }
  return { width, height, data };
}
