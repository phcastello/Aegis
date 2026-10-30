// Development-only review artifacts. The shipped PNG, rather than the recipe's
// reconstruction, is displayed. Diagnostic pixel counts never approve artwork.
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { buildWave } from './derive-wave.mjs';
import { compareWave } from './quality.mjs';
import { cropPng, encodePng, readPng } from './png.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const { values } = parseArgs({ options: {
  output: { type: 'string', default: '/tmp/aegis-ruri-wave-review' },
  record: { type: 'boolean', default: false }
} });
const output = resolve(values.output);
mkdirSync(output, { recursive: true });
const spec = JSON.parse(readFileSync(resolve(root, 'scripts/ruri/wave-spec.json'), 'utf8'));
const variantPath = 'packages/aegis-client/src/assets/ruri-waving-consistent.png';
const original = readPng(resolve(root, spec.canonicalAtlas)), variant = readPng(resolve(root, variantPath));
const result = buildWave(original, spec);
const quality = compareWave(result.canonical, variant, result.allowedMasks, original);
const component = readFileSync(resolve(root, 'packages/aegis-client/src/components/RuriMascot.vue'), 'utf8');
const sequence = name => {
  const line = component.split('\n').find(line => line.startsWith(`const ${name} = `));
  return JSON.parse(line.slice(line.indexOf('['), line.indexOf(']') + 1));
};
const wavingFrames = sequence('wavingFrames'), wavingDurations = sequence('wavingDurations');
const idleFrames = sequence('idleFrames'), idleDurations = sequence('idleDurations');
const sum = numbers => numbers.reduce((total, duration) => total + duration, 0);
const assetInfo = path => ({ path, sha256: createHash('sha256').update(readFileSync(resolve(root, path))).digest('hex') });
const report = {
  assets: {
    canonical: { ...assetInfo(spec.canonicalAtlas), width: original.width, height: original.height },
    derived: { ...assetInfo(variantPath), width: variant.width, height: variant.height }
  },
  reference: { row: 0, column: 0, width: 192, height: 208 },
  diagnostics: quality,
  playback: {
    idleFrames, idleDurations, wavingFrames, wavingDurations,
    idleMs: sum(idleDurations), waveMs: sum(wavingDurations),
    appCycleMs: sum(wavingDurations) + sum(idleDurations) * 2,
    reviewOrder: 'waving → idle → idle → repeat',
    recordedCycles: 0
  },
  visualReview: {
    status: 'requires visual inspection; automated diagnostics do not establish approval',
    criteria: [
      'Compare eyes, face, hood, torso, cloak silhouette and feet with canonical idle.',
      'Inspect each drawn arm/sleeve/palm pose, including elbow bend, wrist gestures and relaxed descent.',
      'Watch two complete real-time cycles at app scale and enlarged scale; reject teleporting, rigid rotation or awkward poses.'
    ]
  }
};

const pngUrl = image => 'data:image/png;base64,' + encodePng(image).toString('base64');
const neutralUrl = pngUrl(result.canonical);
const poseUrls = Array.from({ length: variant.width / 192 }, (_, frame) => pngUrl(cropPng(variant, frame * 192, 0, 192, 208)));
const idleUrls = idleFrames.map(frame => pngUrl(cropPng(original, frame * 192, 0, 192, 208)));
const names = ['Repouso', 'Preparação', 'Elevação / cotovelo', 'Palma se aproxima', 'Palma erguida', 'Aceno para fora', 'Aceno para dentro', 'Retorno relaxado'];
const previewData = { poseUrls, idleUrls, wavingFrames, wavingDurations, idleDurations, names };
const contactFigures = poseUrls.map((src, frame) => `<figure><img src="${src}" alt="Pose ${frame}: ${names[frame]}"><figcaption>${frame}. ${names[frame]}</figcaption></figure>`).join('');
const chronologicalFigures = wavingFrames.map((frame, index) => `<figure><img src="${poseUrls[frame]}" alt="Passo ${index + 1}, pose ${frame}"><figcaption>${index + 1}. pose ${frame} · ${wavingDurations[index]} ms</figcaption></figure>`).join('');
const html = `<!doctype html><html lang="pt-BR"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Ruri — revisão visual do aceno</title><style>
body{margin:24px;background:#101827;color:#e4e9f4;font:15px system-ui}h1{font-size:21px}button{padding:8px 12px;margin-right:8px}img{image-rendering:pixelated}#animation{width:144px;height:156px}#enlarged{width:384px;height:416px}#stage{display:flex;align-items:end;gap:24px;min-height:460px}figure{margin:0}figcaption{font-size:12px;min-height:28px}.contact,.chronological{display:grid;grid-template-columns:repeat(4,192px);gap:8px;width:max-content}.contact img,.chronological img{width:192px;height:208px}#state,#cycles{font-variant-numeric:tabular-nums}p{max-width:1050px}#state{min-height:24px}
</style><h1>Ruri — revisão visual do aceno desenhado</h1>
<p>Aceno ${sum(wavingDurations) / 1000}s → idle ${sum(idleDurations) / 1000}s → idle ${sum(idleDurations) / 1000}s, repetindo. Compare anatomia, manga, palma e silhueta; contagens de pixels são apenas diagnóstico.</p>
<div id="stage"><figure><img id="animation" alt="Ruri em animação na escala do aplicativo"><figcaption>Escala real do aplicativo: 144 × 156</figcaption></figure><figure><img id="enlarged" alt="Mesma animação ampliada"><figcaption>Ampliação: 384 × 416</figcaption></figure><figure><img src="${neutralUrl}" width="192" height="208" alt="Idle canônico"><figcaption>Referência: idle canônico</figcaption></figure></div>
<div id="state"></div><div id="cycles"></div>
<p><button id="pause">Pausar</button><button id="restart">Reiniciar</button><button id="previous">Passo anterior</button><button id="next">Próximo passo</button><label>Pose desenhada <select id="pose">${names.map((name, index) => `<option value="${index}">${index}. ${name}</option>`).join('')}</select></label><button id="inspect">Inspecionar pose</button></p>
<h2>Frames finais e idle de referência</h2><div class="contact"><figure><img src="${neutralUrl}" alt="Referência: idle 0"><figcaption>Referência: idle 0</figcaption></figure>${contactFigures}</div>
<h2>${wavingFrames.length} passos na ordem de reprodução</h2><div class="chronological">${chronologicalFigures}</div>
<script>
const data=${JSON.stringify(previewData)};
const phases=[...data.wavingFrames.map((frame,i)=>({src:data.poseUrls[frame],label:'aceno · passo '+(i+1)+' · pose '+frame+' · '+data.names[frame],ms:data.wavingDurations[i]})),...data.idleUrls.map((src,i)=>({src,label:'idle 1 · '+i,ms:data.idleDurations[i]})),...data.idleUrls.map((src,i)=>({src,label:'idle 2 · '+i,ms:data.idleDurations[i]}))];
let index=0,timer,paused=false,completedCycles=0;
const img=document.querySelector('#animation'),enlarged=document.querySelector('#enlarged'),state=document.querySelector('#state');
function render(step){img.src=enlarged.src=step.src;state.textContent=step.label+' · '+step.ms+' ms';document.querySelector('#cycles').textContent='Ciclos completos: '+completedCycles;document.querySelector('#pause').textContent=paused?'Continuar':'Pausar'}
function play(){clearTimeout(timer);const step=phases[index];render(step);if(!paused&&!document.hidden)timer=setTimeout(()=>{index++;if(index===phases.length){index=0;completedCycles++}play()},step.ms)}
function restart(){index=0;completedCycles=0;paused=false;play()}
function seek(next){paused=true;index=(next+phases.length)%phases.length;play()}
document.querySelector('#pause').onclick=()=>{paused=!paused;play()};
document.querySelector('#restart').onclick=restart;
document.querySelector('#previous').onclick=()=>seek(index-1);document.querySelector('#next').onclick=()=>seek(index+1);
document.querySelector('#inspect').onclick=()=>{paused=true;clearTimeout(timer);const frame=Number(document.querySelector('#pose').value);render({src:data.poseUrls[frame],label:'Inspeção da pose '+frame+' · '+data.names[frame],ms:0})};
document.addEventListener('visibilitychange',play);
window.ruriReview={phases,restart,seek,get completedCycles(){return completedCycles},get index(){return index},pause:()=>{paused=true;play()}};
Promise.all([...data.poseUrls,...data.idleUrls].map(src=>{const image=new Image();image.src=src;return image.decode()})).then(play);
</script></html>`;
writeFileSync(resolve(output, 'animation.html'), html);

const requireClient = createRequire(resolve(root, 'packages/aegis-client/package.json'));
const { chromium } = requireClient('playwright-core');
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
try {
  const context = await browser.newContext({ viewport: { width: 1080, height: 850 }, ...(values.record ? {
    recordVideo: { dir: output, size: { width: 1080, height: 850 } }
  } : {}) });
  const page = await context.newPage();
  await page.setContent(html);
  await page.locator('.chronological img').last().evaluate(img => img.decode());
  await page.locator('.contact').screenshot({ path: resolve(output, 'contact-sheet.png') });
  await page.locator('.chronological').screenshot({ path: resolve(output, 'chronological-sheet.png') });
  for (let frame = 0; frame < poseUrls.length; frame++) {
    await page.selectOption('#pose', String(frame));
    await page.click('#inspect');
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({ path: resolve(output, `pose-${frame}.png`) });
  }
  if (values.record) {
    await page.evaluate(() => { window.scrollTo(0, 0); window.ruriReview.restart(); });
    await page.waitForFunction(() => window.ruriReview.completedCycles >= 2, null, { timeout: report.playback.appCycleMs * 2 + 10000 });
    report.playback.recordedCycles = await page.evaluate(() => window.ruriReview.completedCycles);
    await page.screenshot({ path: resolve(output, 'two-cycles-complete.png') });
    await page.evaluate(() => window.ruriReview.pause());
  }
  const video = page.video();
  await context.close();
  if (video) renameSync(await video.path(), resolve(output, 'animation-two-cycles.webm'));
} finally { await browser.close(); }
writeFileSync(resolve(output, 'report.json'), JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify({ output, waveMs: report.playback.waveMs, cycleMs: report.playback.appCycleMs,
  recordedCycles: report.playback.recordedCycles, visualReview: report.visualReview.status }));
