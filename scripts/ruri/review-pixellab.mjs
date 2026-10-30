// Reproducible, pre-integration review of the owner's current PixelLab export.
// The original idle atlas is used only for the approved idle playback.
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readPxo, buildRefinedWave, sequence, durations, sourcePath } from './refine-pixellab.mjs';
import { cropPng, encodePng, readPng } from './png.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const output = resolve(process.argv[2] ?? resolve(root, 'docs/ruri-pixellab/review'));
mkdirSync(output, { recursive: true });
const source = readPxo(), final = buildRefinedWave(source);
const canonicalPath = resolve(root, 'packages/aegis-client/src/assets/ruri-spritesheet.png');
const canonical = readPng(canonicalPath);
const idleFrames = [0, 1, 3, 4, 5], idleDurations = [1800, 500, 500, 500, 1700];
const sum = values => values.reduce((total, value) => total + value, 0);
const pngUrl = value => 'data:image/png;base64,' + encodePng(value).toString('base64');
const originalUrls = source.frames.map(pngUrl), poseUrls = final.frames.map(pngUrl);
const idleUrls = idleFrames.map(i => pngUrl(cropPng(canonical, i * 192, 0, 192, 208)));
const sourceReference = pngUrl(source.frames[0]);
const figure = (src, label, className = '') => `<figure class="${className}"><img src="${src}" alt="${label}"><figcaption>${label}</figcaption></figure>`;
const contacts = figure(sourceReference, 'Fonte · frame 0 · referência') + poseUrls.map((src, i) => figure(src, `Final · frame ${i}${i < 3 ? ' · preservado' : ' · retocado'}`)).join('');
const chronological = sequence.map((frame, step) => figure(poseUrls[frame], `${step + 1}. frame ${frame} · ${durations[step]} ms`)).join('');
const details = source.frames.map((frame, i) => {
  const before = pngUrl(cropPng(frame, 72, 88, 85, 82));
  const after = pngUrl(cropPng(final.frames[i], 72, 88, 85, 82));
  return `<div class="pair">${figure(before, `Frame ${i} · fonte`)}${figure(after, `Frame ${i} · final`)}</div>`;
}).join('');
const report = {
  source: { path: 'docs/ruri-pixellab/source/ruri-waving.png.pxo', sha256: createHash('sha256').update(readFileSync(sourcePath)).digest('hex'), fps: source.fps, frameCount: source.frames.length },
  final: { atlasSha256: createHash('sha256').update(encodePng(final.atlas)).digest('hex'), correctedFrames: [3, 4, 5, 6, 7, 8, 9], unchangedFrames: [0, 1, 2] },
  playback: { sequence, durations, waveMs: sum(durations), idleFrames, idleDurations, idleMs: sum(idleDurations), cycleMs: sum(durations) + 2 * sum(idleDurations), recordedCycles: 0 },
  review: { stage: 'pre-integration', source: 'Current owner-supplied PixelLab export; no prior wave art.', visualApproval: 'Requires human review. Pixel diagnostics and browser recording do not establish visual approval.' }
};
const data = { poseUrls, originalUrls, idleUrls, sequence, durations, idleDurations };
const html = `<!doctype html><html lang="pt-BR"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Ruri — PixelLab refinado</title>
<style>
*{box-sizing:border-box}body{margin:24px;background:#172035;color:#ecf0ff;font:15px system-ui}h1{font-size:22px}h2{font-size:19px;margin-top:30px}p{max-width:1100px}button,select{padding:8px;margin:4px}img{image-rendering:pixelated}figure{margin:0}figcaption{font-size:12px;min-height:35px;padding:5px}#stage{display:flex;align-items:end;gap:30px;min-height:455px}#animation{width:144px;height:156px}#enlarged{width:384px;height:416px}.reference img{width:192px;height:208px}#state,#cycles{min-height:23px;font-variant-numeric:tabular-nums}.sheet{display:grid;grid-template-columns:repeat(4,192px);gap:12px;padding:16px;background:#172035;width:max-content}.sheet img{width:192px;height:208px}.details{display:grid;grid-template-columns:repeat(2,auto);gap:20px;padding:16px;background:#172035;width:max-content}.pair{display:flex;gap:8px}.pair img{width:255px;height:246px}
</style><h1>Ruri — refinamento da animação atual do PixelLab</h1>
<p>Frames 0–2 preservados. Retoques locais de olhos nos frames 3–9 e mão do aceno nos frames 3–8. Ordem: ${sequence.join(', ')}. Aceno ${sum(durations)} ms → idle 5000 ms → idle 5000 ms.</p>
<div id="stage">${figure('', 'Escala do app · 144 × 156', 'small').replace('<img ', '<img id="animation" ')}${figure('', 'Ampliação · 384 × 416').replace('<img ', '<img id="enlarged" ')}${figure(sourceReference, 'Referência · frame 0 da fonte atual', 'reference')}</div>
<div id="state"></div><div id="cycles"></div><div><button id="pause">Pausar</button><button id="restart">Reiniciar</button><button id="previous">Passo anterior</button><button id="next">Próximo passo</button><label>Modo <select id="mode"><option value="cycle">Aceno → idle → idle</option><option value="wave">Somente aceno</option></select></label></div>
<h2>Frames finais e referência fornecida</h2><div class="sheet contact">${contacts}</div>
<h2>Ordem completa do aceno</h2><div class="sheet chronological">${chronological}</div>
<h2>Olhos e mãos · fonte / final</h2><div class="details">${details}</div>
<script>
const data=${JSON.stringify(data)};
const wave=data.sequence.map((frame,i)=>({src:data.poseUrls[frame],label:'aceno · passo '+(i+1)+' · frame '+frame,ms:data.durations[i]}));
const idle=round=>data.idleUrls.map((src,i)=>({src,label:'idle '+round+' · passo '+(i+1),ms:data.idleDurations[i]}));
let phases=[...wave,...idle(1),...idle(2)],index=0,paused=true,timer,completedCycles=0,started=0;
const animation=document.querySelector('#animation'),enlarged=document.querySelector('#enlarged');
function render(){const step=phases[index];animation.src=enlarged.src=step.src;document.querySelector('#state').textContent=step.label+' · '+step.ms+' ms';document.querySelector('#cycles').textContent='Ciclos completos: '+completedCycles;document.querySelector('#pause').textContent=paused?'Continuar':'Pausar'}
function play(){clearTimeout(timer);render();if(!paused&&!document.hidden)timer=setTimeout(()=>{index++;if(index===phases.length){index=0;completedCycles++}play()},phases[index].ms)}
function restart(){index=0;completedCycles=0;started=performance.now();paused=false;play()}
function seek(step){paused=true;index=(step+phases.length)%phases.length;play()}
document.querySelector('#pause').onclick=()=>{paused=!paused;play()};document.querySelector('#restart').onclick=restart;
document.querySelector('#previous').onclick=()=>seek(index-1);document.querySelector('#next').onclick=()=>seek(index+1);
document.querySelector('#mode').onchange=event=>{phases=event.target.value==='wave'?wave:[...wave,...idle(1),...idle(2)];restart()};
document.addEventListener('visibilitychange',play);
window.ruriReview={restart,seek,pause(){paused=true;play()},get completedCycles(){return completedCycles},get elapsedMs(){return performance.now()-started},get index(){return index}};
window.ruriReady=Promise.all([...data.poseUrls,...data.idleUrls].map(src=>{const image=new Image();image.src=src;return image.decode()})).then(()=>{render();restart()});
</script></html>`;
writeFileSync(resolve(output, 'animation.html'), html);

const requireClient = createRequire(resolve(root, 'packages/aegis-client/package.json'));
const { chromium } = requireClient('playwright-core');
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
try {
  const context = await browser.newContext({ viewport: { width: 1200, height: 850 } });
  const page = await context.newPage();
  await page.setContent(html);
  await page.evaluate(() => window.ruriReady);
  await page.evaluate(() => window.ruriReview.pause());
  for (const [selector, name] of [['.contact', 'contact-sheet.png'], ['.chronological', 'chronological-sheet.png'], ['.details', 'details.png']]) {
    await page.locator(selector).screenshot({ path: resolve(output, name) });
  }
  await context.close();
  const recorded = await browser.newContext({ viewport: { width: 1200, height: 760 }, recordVideo: { dir: output, size: { width: 1200, height: 760 } } });
  const recording = await recorded.newPage();
  await recording.setContent(html);
  await recording.evaluate(() => window.ruriReady);
  await recording.evaluate(() => window.ruriReview.restart());
  await recording.waitForFunction(() => window.ruriReview.completedCycles >= 2, null, { timeout: report.playback.cycleMs * 2 + 10000 });
  const observation = await recording.evaluate(() => {
    window.ruriReview.pause();
    return { recordedCycles: window.ruriReview.completedCycles, elapsedMs: Math.round(window.ruriReview.elapsedMs) };
  });
  Object.assign(report.playback, observation);
  const video = recording.video();
  await recorded.close();
  renameSync(await video.path(), resolve(output, 'animation-two-cycles.webm'));
} finally { await browser.close(); }
writeFileSync(resolve(output, 'report.json'), JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify({ output, ...report.playback }));
