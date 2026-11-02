import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { mkdtemp, cp, writeFile, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';

const require = createRequire(new URL('../../../packages/aegis-client/package.json', import.meta.url));
const { chromium } = require('playwright-core');
const directory = await mkdtemp(join(tmpdir(), 'aegis-reviewer-'));
const assets = new URL('../src-tauri/gen/android/app/src/main/assets/hands-free-reviewer/', import.meta.url);
const wav = Buffer.alloc(48);wav.write('RIFF',0);wav.writeUInt32LE(40,4);wav.write('WAVEfmt ',8);wav.writeUInt32LE(16,16);wav.writeUInt16LE(1,20);wav.writeUInt16LE(1,22);wav.writeUInt32LE(16000,24);wav.writeUInt32LE(32000,28);wav.writeUInt16LE(2,32);wav.writeUInt16LE(16,34);wav.write('data',36);wav.writeUInt32LE(4,40);wav.writeInt16LE(123,44);
const hash = createHash('sha256').update(wav).digest('hex');
const events = ['a','b'].map((id,i)=>({id,activatedAt:`2026-11-01T21:30:0${i}-03:00`,endedAt:null,modelVersion:'aegis-mww-fleurs-dev-2026-10-31',modelSha256:'model-hash',instantaneousScore:i ? null:.8,decisionScore:.6,finalState:'cancelled',endReason:'explicit_user_cancel',transcriptionStatus:'not_started',humanLabel:null,groupId:'shared-session',exportedAt:'2026-11-01T21:35:00-03:00',triggerContext:{path:`${id}-context.wav`,sha256:hash},postActivation:{path:`${id}-post.wav`,sha256:hash}}));
let browser;
try {
  await cp(assets,directory,{recursive:true});
  await writeFile(join(directory,'events.js'),`window.AEGIS_EVENTS_BASE64='${Buffer.from(JSON.stringify(events)).toString('base64')}';`);
  for(const e of events)for(const key of ['triggerContext','postActivation'])await writeFile(join(directory,e[key].path),wav);
  browser=await chromium.launch({executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE,headless:true});
  const page=await browser.newPage({acceptDownloads:true});const errors=[],network=[];
  page.on('pageerror',error=>errors.push(error.message));page.on('request',request=>{if(/^https?:/.test(request.url()))network.push(request.url());});
  await page.goto(pathToFileURL(join(directory,'index.html')).href);
  assert.equal(await page.locator('#label').innerText(),'Não classificada');assert.equal(await page.locator('#progress').innerText(),'0 / 2 classificadas');assert.equal(await page.locator('#export').isDisabled(),true);
  await page.keyboard.press('1');
  await page.locator('#context').evaluate(audio => audio.play());await page.locator('#post').evaluate(audio => audio.play());
  assert.equal(await page.locator('#context').evaluate(audio => audio.duration > 0),true);
  await page.keyboard.press('ArrowRight');await page.keyboard.press('3');
  assert.equal(await page.locator('#label').innerText(),'Incerta');assert.equal(await page.locator('#progress').innerText(),'2 / 2 classificadas');
  await page.reload();assert.equal(await page.locator('#label').innerText(),'Correta');
  await page.locator('#files').setInputFiles(directory);
  const downloadPromise=page.waitForEvent('download');await page.locator('#export').click();const download=await downloadPromise;assert.match(download.suggestedFilename(),/^aegis-dataset-\d{4}-\d\d-\d\d_.*\.zip$/);
  const zipPath=join(directory,'dataset.zip');await download.saveAs(zipPath);
  const report=JSON.parse(execFileSync('python3',['-c',`import json,zipfile,sys,hashlib
with zipfile.ZipFile(sys.argv[1]) as z:
 assert z.testzip() is None
 rows=[json.loads(x) for x in z.read('dataset.jsonl').decode().splitlines()]
 for row in rows:
  for key in ['triggerContext','postActivation']:
   audio=row[key];assert hashlib.sha256(z.read(audio['path'])).hexdigest()==audio['sha256']
 print(json.dumps({'rows':rows,'names':z.namelist()}))`,zipPath],{encoding:'utf8'}));
  assert.deepEqual(report.rows.map(r=>r.humanLabel),['correct','uncertain']);assert.equal(report.rows.every(r=>r.groupId==='shared-session'&&r.split===null),true);assert.deepEqual(report.rows.map(r=>r.id),['a','b']);assert.equal(report.names.filter(n=>n.endsWith('.wav')).length,4);
  await page.keyboard.press('0');assert.equal(await page.locator('#label').innerText(),'Não classificada');
  assert.equal(await page.evaluate(() => {try {window.AegisReviewer.zip(new Array(65536).fill({name:'a',bytes:new Uint8Array()}));return false;}catch(error){return error.message.includes('65.535');}}),true);
  assert.equal(report.rows.every(row=>typeof row.labeledAt==='string'),true);
  assert.deepEqual(errors,[]);assert.deepEqual(network,[]);
  console.log('Offline file:// reviewer: labels, keyboard, persistence, SHA-256, WAV selection, dated dataset ZIP and group provenance passed. No network requests. Synthetic browser fixture, not device validation.');
} finally {await browser?.close();await rm(directory,{recursive:true,force:true});}
