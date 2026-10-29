// Actual Chromium/Vue/CSS with canonical assets; no backend or physical-device claim.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { mkdirSync } from 'node:fs';
import { chromium } from 'playwright-core';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const nativeRequire = createRequire(join(root, 'apps/aegis-node/package.json'));
const { createServer } = await import(nativeRequire.resolve('vite'));
const file = path => '/@fs/' + join(root, path);
const entry = `
import { createApp, h, reactive } from 'vue';
import Sidebar from '${file('packages/aegis-client/src/components/ConversationSidebar.vue')}';
import Ruri from '${file('packages/aegis-client/src/components/RuriMascot.vue')}';
import '${file('packages/aegis-client/src/styles.css')}';
const rows = reactive([
 { id:'a', title:'Nova conversa', titleSource:'default', createdAt:'2026-10-28', updatedAt:'2026-10-28', messageCount:1 },
 { id:'b', title:'Título antigo', titleSource:'generated', createdAt:'2026-10-20', updatedAt:'2026-10-20', messageCount:4, hasUnreadProactive:true }
]);
const state = reactive({ open:true, generating:['a'], ruri:true, counter:0, selected:'a', notice:false, menuClicks:0, noticeClicks:0 });
window.visual = { rows, state, title(id,title,source) { Object.assign(rows.find(r=>r.id===id),{title,titleSource:source}); },
 remove(id) { rows.splice(rows.findIndex(r=>r.id===id),1); },
 add(row) { rows.push({ createdAt:'2026-10-28', updatedAt:'2026-10-28', messageCount:1, ...row }); } };
createApp({ render:()=>h('main',{class:'app-shell'},h('div',{class:['hideout-shell',{'hideout-shell--sidebar-open':state.open}]},[
 h(Sidebar,{ conversations:rows, open:state.open, activeConversationId:state.selected, generatingConversationIds:state.generating }),
 h('section',{class:'chat-panel'},[
  h('header',{class:'chat-header'},[h('div',{class:'conversation-heading'},h('h1','Nova conversa')),h('div',{class:'chat-voice-controls'},h('button',{class:'conversation-menu-trigger',onClick:()=>state.menuClicks++},'⋮'))]),
  h('div',{class:'chat-notices'},state.notice?h('button',{onClick:()=>state.noticeClicks++},'Aviso do dispositivo'):null),
  h('div',{class:'messages'},h('div',{class:'messages-content'},[state.ruri?h('div',{class:'empty-state'},[h(Ruri),h('strong','Nova conversa')]):h('p',{id:'first-message'},'Primeira mensagem'),h('span',{hidden:true},String(state.counter))])),
  h('div',{class:'composer'},h('textarea',{'aria-label':'Mensagem',placeholder:'Digite uma mensagem'}))
 ])
])) }).mount('#app');
`;
const vite = await createServer({ root:join(root,'apps/aegis-node'),server:{host:'127.0.0.1',port:0,strictPort:false,fs:{allow:[root]}}, plugins:[{
 name:'visual-refinements-fixture',
 resolveId(id) { if(id==='/__visual-entry') return '\0visual-refinements'; },
 load(id) { if(id==='\0visual-refinements') return entry; },
 configureServer(server) { server.middlewares.use((req,res,next)=>{
  if(req.url!=='/__visual') return next();
  res.setHeader('Content-Type','text/html');
  res.end('<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="app"></div><script type="module" src="/__visual-entry"></script></body></html>');
 }); }
}] });
await vite.listen();
const browser = await chromium.launch({executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE,headless:true});
const origin = `http://127.0.0.1:${vite.httpServer.address().port}`;
let assertions=0;
const equal=(a,b,label)=>{assert.equal(a,b,label);assertions++;};
const ok=(value,label)=>{assert.ok(value,label);assertions++;};
const errors=[];
const screenshots = '/tmp/aegis-final-refinement-visual';
mkdirSync(screenshots, { recursive: true });
try {
 for(const width of [390,1280]) {
  const page=await browser.newPage({viewport:{width,height:850}});
  page.on('pageerror',error=>errors.push(error.message));
  await page.goto(origin+'/__visual');
  const row=id=>page.locator('.history-item').filter({has:page.locator(`button[aria-label="${id}"]`)});
  const title=()=>page.locator('.history-item').first().locator('strong');
  const ruri=page.locator('.ruri-mascot');
  await ruri.waitFor();
  await page.waitForFunction(()=>document.querySelector('.ruri-mascot').dataset.state==='waving');
  equal(await ruri.getAttribute('data-state'),'waving','first display starts a calm greeting');
  equal(await page.locator('.history-generating-dot').count(),1,'generation dot does not require generated title');
  equal(await page.locator('.unread-dot').count(),1,'unread and generation remain distinct indicators');
  equal(await page.locator('.history-item').nth(1).locator('[data-title-animating]').count(),0,'historical title mounts complete');
  const atlas=await ruri.evaluate(async el=>{
   const url=getComputedStyle(el).backgroundImage.slice(5,-2), img=new Image();img.src=url;await img.decode();
   return {width:img.naturalWidth,height:img.naturalHeight,rendering:getComputedStyle(el).imageRendering};
  });
  equal(atlas.width,1536,'canonical atlas dimensions preserved');equal(atlas.height,2288,'all canonical rows preserved');equal(atlas.rendering,'pixelated','pixel art uses crisp scaling');
  await page.waitForFunction(()=>document.querySelector('.ruri-mascot').dataset.state==='idle');
  await page.evaluate(()=>window.visual.state.counter++);
  equal(await ruri.getAttribute('data-state'),'idle','reactive rendering does not repeat greeting');
  await page.evaluate(()=>window.visual.title('a','Planejar as provas','generated'));
  await page.waitForFunction(()=>document.querySelector('[data-title-animating]'));
  await page.waitForTimeout(100);
  const erasing=await title().textContent();
  ok(erasing.length<13 && erasing.length>0,'old title erases quickly behind the dot');
  equal(await title().getAttribute('aria-label'),'Planejar as provas','real title remains accessible during animation');
  await page.waitForTimeout(320);
  const writing=await title().textContent();
  ok('Planejar as provas'.startsWith(writing) && writing.length<18,'complete title types progressively after erasing');
  await page.waitForFunction(()=>!document.querySelector('[data-title-animating]'));
  equal(await title().textContent(),'Planejar as provas','animation reaches the authoritative title');
  await page.evaluate(()=>{window.visual.title('a','Planejar as provas','generated');window.visual.state.open=false;});
  await page.evaluate(()=>window.visual.state.open=true);
  equal(await page.locator('[data-title-animating]').count(),0,'refresh and sidebar reopen do not repeat title animation');
  await page.evaluate(()=>window.visual.title('a','Título manual','manual'));
  equal(await title().textContent(),'Título manual','manual rename applies immediately');
  equal(await page.locator('[data-title-animating]').count(),0,'manual rename never animates');
  await page.evaluate(()=>{window.visual.title('a','Nova conversa','default');window.visual.state.selected='b';});
  await page.evaluate(()=>window.visual.title('a','Chegou depois do turno','generated'));
  await page.waitForFunction(()=>document.querySelector('[data-title-animating]'));
  equal(await page.locator('.history-item').nth(1).locator('strong').textContent(),'Título antigo','late title stays in its owner row');
  await page.evaluate(()=>window.visual.title('a','Correção manual','manual'));
  await page.waitForTimeout(150);
  equal(await title().textContent(),'Correção manual','manual rename cancels an obsolete automatic animation');
  await page.evaluate(()=>window.visual.state.generating=[]);
  equal(await page.locator('.history-generating-dot').count(),0,'generation marker disappears at completion');
  await page.evaluate(()=>window.visual.state.open=false);
  await page.waitForTimeout(260);
  await page.screenshot({path:join(screenshots,width===390?'android-empty.png':'desktop-empty.png')});
  await page.evaluate(()=>window.visual.state.ruri=false);
  const geometry=await page.locator('.chat-panel').evaluate(el=>{
   const header=el.querySelector('.chat-header'),messages=el.querySelector('.messages'),first=el.querySelector('#first-message');
   return {height:header.getBoundingClientRect().height,headerTop:header.getBoundingClientRect().top,panelTop:el.getBoundingClientRect().top,messagesTop:messages.getBoundingClientRect().top,firstTop:first.getBoundingClientRect().top,pointer:getComputedStyle(header).pointerEvents,fade:getComputedStyle(el,'::before').backgroundImage,background:getComputedStyle(header).backgroundColor};
  });
  equal(geometry.height,0,'floating controls reserve no header row');equal(geometry.messagesTop,geometry.panelTop,'messages begin at actual content top');
  equal(geometry.background,'rgba(0, 0, 0, 0)','controls have no bar background');equal(geometry.pointer,'none','transparent header area accepts gestures beneath');
  if(width===1280) await page.screenshot({path:join(screenshots,'header-message.png')});
  ok(geometry.fade.includes('linear-gradient'),'fade belongs to content independently of controls');ok(geometry.firstTop>geometry.headerTop+54,'first message starts below control hit targets');
  equal(await page.locator('.chat-notices').evaluate(el=>el.getBoundingClientRect().height),0,'empty native notice slot reserves no strip');
  await page.evaluate(()=>window.visual.state.notice=true);
  await page.locator('.conversation-menu-trigger').click();
  equal(await page.evaluate(()=>window.visual.state.menuClicks),1,'active notice cannot intercept the floating menu');
  await page.getByRole('button',{name:'Aviso do dispositivo'}).click();
  equal(await page.evaluate(()=>window.visual.state.noticeClicks),1,'notice actions remain clickable');
  await page.evaluate(()=>window.visual.state.notice=false);
  await page.emulateMedia({reducedMotion:'reduce'});
  await page.evaluate(()=>{window.visual.state.ruri=true;window.visual.state.open=true;window.visual.title('a','Nova conversa','default');});
  await page.evaluate(()=>window.visual.title('a','Sem animação','generated'));
  await page.waitForTimeout(200);
  equal(await title().textContent(),'Sem animação','reduced motion applies full title immediately');
  equal(await page.locator('[data-title-animating]').count(),0,'reduced motion suppresses title dot');
  equal(await ruri.getAttribute('data-state'),'idle','reduced motion skips greeting');equal(await ruri.getAttribute('data-frame'),'0','reduced motion keeps a static idle frame');
  await page.emulateMedia({reducedMotion:'no-preference'});
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));});
  const hiddenFrame=await ruri.getAttribute('data-frame');await page.waitForTimeout(450);
  equal(await ruri.getAttribute('data-frame'),hiddenFrame,'hidden document pauses sprite work');
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:false});document.dispatchEvent(new Event('visibilitychange'));window.visual.state.ruri=false;});
  // Removing a row mid-animation disposes its timer without touching a new row.
  await page.evaluate(()=>window.visual.title('a','Nova conversa','default'));
  await page.evaluate(()=>window.visual.title('a','Resultado descartado','generated'));
  await page.waitForFunction(()=>document.querySelector('[data-title-animating]'));
  await page.evaluate(()=>{window.visual.remove('a');window.visual.add({id:'c',title:'Outra conversa',titleSource:'generated'});});
  await page.waitForTimeout(400);
  equal(await page.locator('.history-item').last().locator('strong').textContent(),'Outra conversa','unmount cancels title timer across rows');
  await page.close();
 }
 // Browser clock exercises complete visible cycles without waiting 26 real seconds.
 for (const width of [390,1280]) {
  const page=await browser.newPage({viewport:{width,height:850}});
  page.on('pageerror',error=>errors.push(error.message));
  await page.clock.install();
  await page.clock.pauseAt(new Date(Date.now()+100));
  await page.goto(origin+'/__visual');
  const ruri=page.locator('.ruri-mascot');
  await page.locator('.ruri-mascot[data-state="waving"]').waitFor();
  await page.evaluate(()=>window.visual.state.open=false);
  const pose=async()=>ruri.evaluate(el=>({state:el.dataset.state,frame:Number(el.dataset.frame),position:getComputedStyle(el).backgroundPosition}));
  await page.getByRole('textbox',{name:'Mensagem'}).fill('Continuo digitando enquanto a Ruri acena');
  // The first hold must last half a second, then the hand returns through the
  // raised pose instead of jumping directly from the outward hand to rest.
  await page.clock.runFor(499);
  equal((await pose()).frame,0,'greeting holds its resting pose before raising the hand');
  const wave=[1,2,1,2,1,3];
  for (const [index,hold] of [1,350,400,350,400,350].entries()) {
   await page.clock.runFor(hold);
   const current=await pose();
   equal(current.state,'waving','arm transition stays within one wave sequence');
   equal(current.frame,wave[index],'hand follows the outward-and-return sequence');
   equal(current.position,`${wave[index]*-144}px -468px`,'wave uses the intended canonical atlas cell');
  }
  await page.screenshot({path:join(screenshots,`${width}-wave-return.png`)});
  await page.clock.runFor(600);
  for (let cycle=0;cycle<2;cycle++) {
   equal((await pose()).state,'idle','each wave is followed by two idle cycles');
   equal((await pose()).frame,0,'idle begins on the stable resting pose');
   for (const [index,hold] of [1800,500,500,500].entries()) {
    await page.clock.runFor(hold);
    const current=await pose();
    equal(current.frame,[1,3,4,5][index],'idle omits the abrupt closed-eye face');
    equal(current.position,`${current.frame*-144}px 0px`,'idle remains aligned on the canonical row');
   }
   await page.clock.runFor(1699);
   equal((await pose()).state,'idle','a complete idle cycle lasts five seconds');
   await page.clock.runFor(1);
  }
  equal((await pose()).state,'waving','the next wave repeats after exactly two idle cycles');
  equal(await page.getByRole('textbox',{name:'Mensagem'}).inputValue(),'Continuo digitando enquanto a Ruri acena','typing is preserved across the repeating cycle');
  equal(await page.getByRole('textbox',{name:'Mensagem'}).evaluate(el=>el===document.activeElement),true,'repeated waving leaves the composer focused');
  // Pausing never catches up a hidden animation or leaves recurring work while
  // reduced motion is enabled. Test both document and intersection visibility.
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));});
  const hidden=await pose();await page.clock.runFor(30000);
  equal(JSON.stringify(await pose()),JSON.stringify(hidden),'hidden document freezes the entire sequence');
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:false});document.dispatchEvent(new Event('visibilitychange'));});
  await page.clock.runFor(500);equal((await pose()).frame,1,'visibility resumes from the held pose without catching up');
  await ruri.evaluate(el=>el.style.transform='translateY(2000px)');
  // IntersectionObserver delivery belongs to the browser rendering lifecycle.
  await page.clock.runFor(100);
  await page.waitForTimeout(100);
  const offscreen=await pose();await page.clock.runFor(30000);
  equal(JSON.stringify(await pose()),JSON.stringify(offscreen),'offscreen mascot pauses sprite work');
  await ruri.evaluate(el=>el.style.transform='');
  await page.clock.runFor(100);await page.waitForTimeout(100);
  await page.emulateMedia({reducedMotion:'reduce'});
  // MediaQueryList changes are delivered by rendering, independently of the
  // paused timer clock. Wait for that event before checking the static pose.
  await page.locator('.ruri-mascot[data-state="idle"][data-frame="0"]').waitFor();
  equal((await pose()).state,'idle','enabling reduced motion cancels a running wave');
  equal((await pose()).frame,0,'reduced motion selects the stable resting pose');
  await page.clock.runFor(30000);
  equal((await pose()).frame,0,'reduced motion stays static across several potential cycles');
  await page.screenshot({path:join(screenshots,`${width}-reduced-motion.png`)});
  await page.close();
 }
 equal(errors.length,0,'no browser runtime errors');
 console.log(`PASS visual refinements: ${assertions} assertions, Android-width and desktop-width Chromium, canonical Ruri/title/header/reduced motion. Physical Android/Windows not exercised.`);
} finally {await browser.close();await vite.close();}
