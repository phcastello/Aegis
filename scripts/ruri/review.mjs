// Generate review evidence only. Canvas is used for previews, never to encode
// production sprite pixels; production assets use the lossless PNG helper.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { buildWave } from './derive-wave.mjs';
import { compareWave, protectedRegions } from './quality.mjs';
import { cropPng, encodePng, readPng } from './png.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const { values } = parseArgs({ options: { output: { type: 'string', default: '/tmp/aegis-ruri-identity-final' } } });
const output = resolve(values.output);
mkdirSync(output, { recursive: true });
const spec = JSON.parse(readFileSync(resolve(root, 'scripts/ruri/wave-spec.json'), 'utf8'));
const variantPath = 'packages/aegis-client/src/assets/ruri-waving-consistent.png';
const original = readPng(resolve(root, spec.canonicalAtlas)), variant = readPng(resolve(root, variantPath));
const result = buildWave(original, spec), quality = compareWave(result.canonical, variant, result.allowedMasks, original);
assert.equal(quality.changedStaticPixels, 0, 'Cannot create approved-looking evidence for inconsistent static pixels');
const component = readFileSync(resolve(root, 'packages/aegis-client/src/components/RuriMascot.vue'), 'utf8');
const sequence = name => {
  const line = component.split('\n').find(line => line.startsWith(`const ${name} = `));
  return JSON.parse(line.slice(line.indexOf('['), line.indexOf(']') + 1));
};
const wavingFrames = sequence('wavingFrames'), wavingDurations = sequence('wavingDurations');
const idleFrames = sequence('idleFrames'), idleDurations = sequence('idleDurations');
const sum = numbers => numbers.reduce((total, duration) => total + duration, 0);
const legacy = compareWave(result.canonical, cropPng(original, 0, 624, 768, 208),
  Array.from({ length: 4 }, () => result.unionMask), original);
const assetInfo = path => ({ path, sha256: createHash('sha256').update(readFileSync(resolve(root, path))).digest('hex') });
const report = {
  assets: { canonical: { ...assetInfo(spec.canonicalAtlas), width: original.width, height: original.height },
    derived: { ...assetInfo(variantPath), width: variant.width, height: variant.height } },
  reference: { row: 0, column: 0, width: 192, height: 208 }, protectedRegions,
  ...quality,
  palette: { newlyBlendedPixels: quality.frames.reduce((total, frame) => total + frame.newColorPixels, 0),
    explanation: 'Only local source-over of the original translucent arm edge over canonical cloak pixels; validated independently in ruri-consistency.test.mjs.' },
  oldWave: { changedStaticPixels: legacy.changedStaticPixels, frames: legacy.frames.map(frame =>
    ({ index: frame.index, changedPixels: frame.changedPixels, outsideArm: frame.outsideMask, regions: frame.namedRegions })) },
  playback: { idleFrames, idleDurations, wavingFrames, wavingDurations, idleMs: sum(idleDurations), waveMs: sum(wavingDurations),
    appCycleMs: sum(wavingDurations) + sum(idleDurations) * 2, reviewOrder: 'idle → waving → idle → repeat' },
  limits: 'Exact PNG comparisons establish static-region fidelity. Arm anatomy, occlusion, pixel-art continuity and device appearance also require human visual review.'
};
writeFileSync(resolve(output, 'report.json'), JSON.stringify(report, null, 2) + '\n');

const pngUrl = image => 'data:image/png;base64,' + encodePng(image).toString('base64');
const neutralUrl = pngUrl(result.canonical);
const poseUrls = result.frames.map(pngUrl);
const idleUrls = idleFrames.map(frame => pngUrl(cropPng(original, frame * 192, 0, 192, 208)));
const previewData = { neutralUrl, poseUrls, idleUrls, wavingFrames, wavingDurations, idleDurations };
const html = `<!doctype html><html lang="pt-BR"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Ruri — revisão de identidade</title><style>
body{margin:24px;background:#101827;color:#e4e9f4;font:15px system-ui}h1{font-size:21px}button{padding:8px 16px;margin-right:8px}#animation{width:192px;height:208px;image-rendering:pixelated}#stage{background:#101827;display:flex;align-items:center;gap:24px}figure{margin:0}figcaption{font-size:12px;min-height:28px}.contact{display:grid;grid-template-columns:repeat(4,192px);gap:8px;width:max-content}.contact img{width:192px;height:208px;image-rendering:pixelated}#state{font-variant-numeric:tabular-nums}p{max-width:850px}
</style><h1>Ruri — base canônica e aceno derivado</h1>
<p>Reprodução: idle ${sum(idleDurations) / 1000}s → aceno ${sum(wavingDurations) / 1000}s → idle ${sum(idleDurations) / 1000}s. Todas as poses usam o mesmo enquadramento e escala. As regiões estáticas foram comparadas pixel a pixel; este preview também exige inspeção visual.</p>
<div id="stage"><figure><img id="animation" alt="Ruri em animação"><figcaption id="state"></figcaption></figure><figure><img src="${neutralUrl}" width="192" height="208" style="image-rendering:pixelated" alt="Idle canônico"><figcaption>Referência neutra: idle 0</figcaption></figure></div>
<p><button id="pause">Pausar</button><button id="restart">Reiniciar</button><label>Escala <select id="scale"><option value="1">1×</option><option value="2">2×</option></select></label></p>
<h2>Referência + 15 passos em ordem cronológica</h2><div class="contact">${[null, ...wavingFrames].map((frame, index) =>
  `<figure><img src="${frame === null ? neutralUrl : poseUrls[frame]}" alt="${index === 0 ? 'Referência' : `Passo ${index}, pose ${frame}`}"><figcaption>${index === 0 ? 'Referência: idle 0' : `${index}. pose ${frame} · ${wavingDurations[index - 1]} ms`}</figcaption></figure>`).join('')}</div>
<script>
const data=${JSON.stringify(previewData)};
const phases=[...data.idleUrls.map((src,i)=>({src,label:'idle inicial · '+i,ms:data.idleDurations[i]})),...data.wavingFrames.map((frame,i)=>({src:data.poseUrls[frame],label:'aceno · passo '+(i+1)+' · pose '+frame,ms:data.wavingDurations[i]})),...data.idleUrls.map((src,i)=>({src,label:'idle final · '+i,ms:data.idleDurations[i]}))];
let index=0,timer,paused=false;const img=document.querySelector('#animation'),state=document.querySelector('#state');
function play(){clearTimeout(timer);const step=phases[index];img.src=step.src;state.textContent=step.label+' · '+step.ms+' ms';if(!paused&&!document.hidden)timer=setTimeout(()=>{index=(index+1)%phases.length;play()},step.ms)}
document.querySelector('#pause').onclick=()=>{paused=!paused;document.querySelector('#pause').textContent=paused?'Continuar':'Pausar';play()};
document.querySelector('#restart').onclick=()=>{index=0;play()};document.addEventListener('visibilitychange',play);
document.querySelector('#scale').onchange=e=>{const scale=Number(e.target.value);img.style.width=(192*scale)+'px';img.style.height=(208*scale)+'px'};
play();window.ruriReview={phases,restart:()=>{index=0;play()}};
</script></html>`;
writeFileSync(resolve(output, 'animation.html'), html);

const requireClient = createRequire(resolve(root, 'packages/aegis-client/package.json'));
const { chromium } = requireClient('playwright-core');
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1600, height: 1800 } });
  await page.setContent(html);
  await page.locator('.contact img').last().evaluate(img => img.decode());
  await page.locator('.contact').screenshot({ path: resolve(output, 'contact-sheet.png') });
  // A second sheet makes the deliberately small edit domain visible without
  // disguising which canonical pixels are allowed to be occluded.
  const overlays = await page.evaluate(({ urls, masks, regions }) => {
    return Promise.all(urls.map(async (src, index) => {
      const img = new Image(); img.src = src; await img.decode();
      const canvas = document.createElement('canvas'); canvas.width = 192; canvas.height = 208;
      const context = canvas.getContext('2d'); context.drawImage(img, 0, 0);
      context.fillStyle = 'rgba(255,170,40,.35)';
      masks[index].forEach((value, pixel) => { if (value) context.fillRect(pixel % 192, Math.floor(pixel / 192), 1, 1); });
      context.strokeStyle = 'rgba(70,255,180,.7)'; context.lineWidth = 1;
      for (const [x0, y0, x1, y1] of Object.values(regions)) context.strokeRect(x0 + .5, y0 + .5, x1 - x0, y1 - y0);
      return canvas.toDataURL();
    }));
  }, { urls: poseUrls, masks: result.allowedMasks.map(mask => [...mask]), regions: protectedRegions });
  await page.setContent(`<body style="margin:8px;background:#101827;color:#e4e9f4;font:14px system-ui"><p>Âmbar: braço/oclusão local permitida. Verde: regiões protegidas independentes.</p><div style="display:grid;grid-template-columns:repeat(4,192px);gap:8px;width:max-content">${overlays.map((src, index) => `<figure style="margin:0"><img src="${src}" width="192" height="208" style="image-rendering:pixelated"><figcaption>Pose ${index}</figcaption></figure>`).join('')}</div></body>`);
  await page.locator('img').last().evaluate(img => img.decode());
  await page.locator('body').screenshot({ path: resolve(output, 'protected-regions.png') });
} finally { await browser.close(); }
console.log(JSON.stringify({ output, staticPixelsCompared: quality.checkedStaticPixels, staticDifferences: quality.changedStaticPixels,
  locallyBlendedPixels: report.palette.newlyBlendedPixels, waveMs: report.playback.waveMs, cycleMs: report.playback.appCycleMs }));
