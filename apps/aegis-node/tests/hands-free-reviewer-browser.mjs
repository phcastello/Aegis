import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { mkdtemp, mkdir, cp, writeFile, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
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
  await page.locator('[data-intent="not_directed"]').click();
  await page.locator('#context').evaluate(audio => audio.play());await page.locator('#post').evaluate(audio => audio.play());
  assert.equal(await page.locator('#context').evaluate(audio => audio.duration > 0),true);
  await page.keyboard.press('ArrowRight');await page.keyboard.press('3');
  assert.equal(await page.locator('#label').innerText(),'Incerta');assert.equal(await page.locator('#progress').innerText(),'2 / 2 classificadas');
  await page.reload();assert.equal(await page.locator('#label').innerText(),'Correta');
  assert.equal(await page.locator('[data-intent="not_directed"]').getAttribute('aria-pressed'), 'true');
  await page.locator('#files').setInputFiles(directory);
  const downloadPromise=page.waitForEvent('download');await page.locator('#export').click();const download=await downloadPromise;assert.match(download.suggestedFilename(),/^aegis-dataset-\d{4}-\d\d-\d\d_.*\.zip$/);
  const zipPath=join(directory,'dataset.zip');await download.saveAs(zipPath);
  const report=JSON.parse(execFileSync(process.platform === 'win32' ? 'python' : 'python3',['-c',`import json,zipfile,sys,hashlib
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
  assert.deepEqual(report.rows.map(row => row.commandLabel), ['not_directed', null]);
  const android = '1d9ef396-f410-42d9-905e-d366ab6cd989';
  const windows = '2d9ef396-f410-42d9-905e-d366ab6cd989';
  const androidEvent = '808586f7-98ec-4ae2-8c9d-de16d3511f52';
  const windowsEvent = '908586f7-98ec-4ae2-8c9d-de16d3511f52';
  const localEvent = 'a08586f7-98ec-4ae2-8c9d-de16d3511f52';
  const episode = 'b08586f7-98ec-4ae2-8c9d-de16d3511f52';
  const volume = join(directory, 'volume'), exported = join(directory, 'central.zip'), review = join(directory, 'central');
  const python = process.platform === 'win32' ? 'python' : 'python3';
  for (const node of [android, windows]) await mkdir(join(volume, node), { recursive: true });
  const recordings = [
    { node: android, event: androidEvent, groupId: episode, disposition: 'election_lost' },
    { node: windows, event: windowsEvent, groupId: episode, disposition: 'activated' },
    { node: windows, event: localEvent, groupId: localEvent, disposition: 'verifier_rejected' },
  ];
  for (const [index, { node, event, groupId, disposition }] of recordings.entries()) {
    const audio = Buffer.from(wav);
    audio.writeInt16LE(123 + index, 44);
    await writeFile(join(volume, node, event + '.json'), JSON.stringify({
      schemaVersion: 1, activatedAt: events[0].activatedAt, groupId, origin: 'wake_word',
      disposition, commandDisposition: 'unknown', wav: audio.toString('base64'),
      sha256: createHash('sha256').update(audio).digest('hex'),
    }));
  }
  execFileSync(python, [fileURLToPath(new URL('../../../scripts/hands-free/export-central-dataset.py', import.meta.url)), volume, exported]);
  execFileSync(python, ['-m', 'zipfile', '-e', exported, review]);
  const manifest = (await readFile(join(review, 'manifest.jsonl'), 'utf8')).trim().split('\n').map(line => JSON.parse(line));
  assert.deepEqual(manifest.map(row => [row.nodeId, row.originalEventId, row.groupId]),
    recordings.map(({ node, event, groupId }) => [node, event, groupId]));
  await page.goto(pathToFileURL(join(review, 'index.html')).href);
  assert.match(await page.locator('#technical').innerText(), /election_lost/);
  assert.equal(await page.locator('#export').isDisabled(), true);
  await page.locator('[data-intent="directed"]').click();
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('1');
  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('2');
  await page.locator('#files').setInputFiles(review);
  const centralRows = await page.evaluate(async () => (await window.AegisReviewer.buildDataset()).rows);
  const [centralRow] = centralRows;
  assert.deepEqual(centralRows.map(row => row.groupId), [episode, episode, localEvent]);
  assert.deepEqual(centralRows.map(row => row.split), [null, null, null]);
  assert.equal(new Set(centralRows.map(row => row.triggerContext.sha256)).size, 3);
  assert.deepEqual(centralRows.map(row => row.commandLabel), ['directed', null, null]);
  assert.deepEqual(centralRows.map(row => row.humanLabel), [null, 'correct', 'false_positive']);
  assert.equal(centralRow.triggerContext.sha256, hash);
  assert.equal(centralRow.provenance.originalEventId, androidEvent);
  assert.deepEqual(errors,[]);assert.deepEqual(network,[]);
  console.log('Offline file:// reviewer: labels, keyboard, persistence, SHA-256, WAV selection, dated dataset ZIP and group provenance passed. No network requests. Synthetic browser fixture, not device validation.');
} finally {await browser?.close();await rm(directory,{recursive:true,force:true});}
