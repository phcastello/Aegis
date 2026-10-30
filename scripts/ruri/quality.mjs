// Secondary integrity diagnostics for the published PNG. Visual review decides
// whether the drawings and timed movement are acceptable.
import { cropPng } from './png.mjs';

// Half-open coordinates in the canonical 192×208 cell. The chin is separate
// from the right hood edge, which the raised hand can legitimately occlude.
export const protectedRegions = {
  eyes: [72, 94, 120, 123], face: [68, 80, 120, 123], chin: [78, 123, 113, 130],
  upperHood: [51, 52, 141, 102], clasp: [95, 124, 105, 138],
  leftTorsoAndCape: [56, 130, 98, 167], lowerCape: [49, 167, 142, 188], feet: [66, 188, 125, 208]
};

export function alphaBbox(image, region = [0, 0, image.width, image.height]) {
  const box = [image.width, image.height, -1, -1];
  for (let y = region[1]; y < region[3]; y++) for (let x = region[0]; x < region[2]; x++) {
    if (!image.data[(y * image.width + x) * 4 + 3]) continue;
    box[0] = Math.min(box[0], x); box[1] = Math.min(box[1], y);
    box[2] = Math.max(box[2], x); box[3] = Math.max(box[3], y);
  }
  return box;
}

export function compareWave(canonical, variant, allowedMasks, sourceAtlas) {
  const { width, height } = canonical;
  const sourceColors = new Set();
  for (let i = 0; i < sourceAtlas.data.length; i += 4) sourceColors.add(sourceAtlas.data.readUInt32BE(i));
  const union = new Uint8Array(width * height);
  const frames = allowedMasks.map((mask, index) => {
    const frame = cropPng(variant, index * width, 0, width, height);
    const namedRegions = Object.fromEntries(Object.entries(protectedRegions).map(([name, box]) =>
      [name, { box, checkedPixels: (box[2] - box[0]) * (box[3] - box[1]), changedPixels: 0 }]));
    let changedPixels = 0, outsideMask = 0, alphaDecreases = 0, newColorPixels = 0, maskPixels = 0, borderAlpha = 0;
    const newColors = new Set(), changedBbox = [width, height, -1, -1];
    for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
      const p = y * width + x, i = p * 4;
      if (mask[p]) {
        union[p] = 1; maskPixels++;
      }
      if ((x === 0 || y === 0 || x === width - 1 || y === height - 1) && frame.data[i + 3]) borderAlpha++;
      if (frame.data[i + 3] < canonical.data[i + 3]) alphaDecreases++;
      const color = frame.data.readUInt32BE(i);
      if (color === canonical.data.readUInt32BE(i)) continue;
      changedPixels++;
      changedBbox[0] = Math.min(changedBbox[0], x); changedBbox[1] = Math.min(changedBbox[1], y);
      changedBbox[2] = Math.max(changedBbox[2], x); changedBbox[3] = Math.max(changedBbox[3], y);
      if (!mask[p]) outsideMask++;
      if (!sourceColors.has(color)) { newColorPixels++; newColors.add(color); }
      for (const region of Object.values(namedRegions)) {
        const [x0, y0, x1, y1] = region.box;
        if (x >= x0 && x < x1 && y >= y0 && y < y1) region.changedPixels++;
      }
    }
    return { index, changedPixels, changedBbox: changedPixels ? changedBbox : null, outsideMask,
      checkedStaticPixels: width * height - maskPixels, maskPixels,
      alphaDecreases, borderAlpha, bbox: alphaBbox(frame), feetBbox: alphaBbox(frame, protectedRegions.feet),
      newColorPixels, newColorCount: newColors.size, namedRegions };
  });
  return { referenceBbox: alphaBbox(canonical), referenceFeetBbox: alphaBbox(canonical, protectedRegions.feet),
    unionMaskPixels: union.reduce((sum, value) => sum + value, 0),
    checkedStaticPixels: frames.reduce((sum, frame) => sum + frame.checkedStaticPixels, 0),
    changedStaticPixels: frames.reduce((sum, frame) => sum + frame.outsideMask, 0),
    frames };
}
