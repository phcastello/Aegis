// Real browser clicks against the production PlacesPanel, PlaceMap and SettingsShell.
// Only native IPC is replaced; fixtures never touch personal data or the real backend.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { createServer } from 'vite';
import vue from '@vitejs/plugin-vue';
const appRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = resolve(appRoot, '../..');
const require = createRequire(join(repoRoot, 'packages/aegis-client/package.json'));
const { chromium } = require('playwright-core');
const virtualId = 'virtual:aegis-places-browser';
const fixtureEntry = `import {createApp,h,ref} from 'vue';
import PlacesPanel from '/src/components/PlacesPanel.vue';
import SettingsShell from '/@fs/${join(repoRoot,'packages/aegis-client/src/components/SettingsShell.vue')}';
import '/@fs/${join(repoRoot,'packages/aegis-client/src/styles.css')}';
import '/src/styles.css';
createApp({setup(){const section=ref('places'),dirty=ref(false),shell=ref(),places=ref();window.reopen=()=>section.value='places';window.addEventListener('aegis-back',()=>{if(!places.value?.requestBack())shell.value?.requestClose()});return ()=>h(SettingsShell,{ref:shell,section:section.value,dirty:dirty.value,onClose:()=>section.value=null,onNavigate:value=>section.value=value,onDiscard:()=>dirty.value=false},()=>section.value==='places'?h(PlacesPanel,{ref:places,platform:'android',onDirty:value=>dirty.value=value}):h('p','Outra seção'));}}).mount('#app');`;
const server = await createServer({root:appRoot,configFile:false,cacheDir:join(appRoot,'node_modules/.vite-places-browser'),resolve:{dedupe:['vue']},plugins:[vue(),{
  name:'aegis-places-browser-fixture',resolveId:id=>id===virtualId?'\0'+virtualId:null,load:id=>id==='\0'+virtualId?fixtureEntry:null,
  configureServer(vite){vite.middlewares.use('/__places_browser__',async(_request,response,next)=>{try{const html=await vite.transformIndexHtml('/__places_browser__',`<!doctype html><html lang="pt-BR"><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"><body><div id="app"></div><script type="module">import '${virtualId}';</script></body></html>`);response.setHeader('Content-Type','text/html; charset=utf-8');response.end(html);}catch(error){next(error);}});}
}],server:{host:'127.0.0.1',port:0,hmr:false,watch:null,fs:{allow:[repoRoot]}}});
const screenshotDir = resolve(process.argv[2] ?? process.env.PLACES_SCREENSHOT_DIR ?? join(repoRoot, 'artifacts/v0.7.0-completion/ui/places'));
mkdirSync(screenshotDir, { recursive: true });
const sources=['apps/aegis-node/src/components/PlacesPanel.vue','apps/aegis-node/src/components/PlaceMap.vue','apps/aegis-node/src/services/placeMapGuidance.ts','packages/aegis-client/src/components/SettingsShell.vue','packages/aegis-client/src/styles.css'];
const report={rendered:'Production PlacesPanel.vue, PlaceMap.vue and SettingsShell.vue; isolated native IPC fixtures, no real backend',fontScaling:'CSS root font150/200 percent and reduced500px viewport simulate large font and keyboard; physical Android keyboard/font test remains pending',sourceHashes:Object.fromEntries(sources.map(path=>[path,createHash('sha256').update(readFileSync(join(repoRoot,path))).digest('hex')])),cases:[],pageErrors:[],failures:[]};
let activePage;
async function assertInViewport(locator, content) {const box=await locator.boundingBox(),view=await content.boundingBox();assert.ok(box&&view);const visible=Math.min(box.y+box.height,view.y+view.height)-Math.max(box.y,view.y);assert.ok(visible>=Math.min(44,box.height),'Feedback must be perceptible inside the scrolled viewport');}

let browser;
try {
  await server.listen();
  const address = server.httpServer.address(); assert.ok(address && typeof address === 'object');
  const base = `http://127.0.0.1:${address.port}`;
  browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
  for (const width of [320, 360, 412]) for (const fontScale of [1, 1.5, 2]) {
    const context = await browser.newContext({ viewport: { width, height: 844 }, isMobile: true, hasTouch: true });
    await context.addInitScript(() => {
      const places = Array.from({ length: 30 }, (_, index) => ({ id: `place-${index}`, name: `Lugar ${String(index).padStart(2,'0')}`, type: 'property', parentId: null, aliases: [], revision: 1, circles: [{ id: `circle-${index}`, latitude: -23.55, longitude: -46.63, radiusMeters: 65 }, { id: `circle-b-${index}`, latitude: -23.5503, longitude: -46.63, radiusMeters: 65 }] }));
      window.fixture = { accuracy: 13, age: 0, unavailable: false, searchFailure: false, conflict: false, calls: [], saved: [] };
      window.__TAURI_INTERNALS__ = { invoke: async (command,args) => {
        const payload = JSON.parse(JSON.stringify(args ?? {})); window.fixture.calls.push({command,...payload});
        if (command === 'node_places') {
          if (args.operation === 'list') {if(window.fixture.failNextList){window.fixture.failNextList=false;throw Error('offline-after-write');}return structuredClone(places);}
          if (args.operation === 'config') return { tileUrl: null, attribution: '', geocodingConfigured: true, geocoderProvider: 'photon' };
          if (args.operation === 'search') { if (window.fixture.searchFailure) throw Error('raw-private-error'); return { configured: true, results: [{ name: 'Endereço sintético', latitude: -23.56, longitude: -46.64 }] }; }
          if (['update','create'].includes(args.operation)) { if(window.fixture.saveDelay) await new Promise(done=>setTimeout(done,window.fixture.saveDelay)); if (window.fixture.conflict) throw Error('revision_conflict internal-private-detail'); const previous = places.find(item=>item.id===args.id); const saved = { ...args.payload, id: args.id ?? crypto.randomUUID(), revision: (previous?.revision ?? 0)+1 }; if (previous) Object.assign(previous,saved); else places.push(saved); window.fixture.saved.push(structuredClone(saved)); return structuredClone(saved); }
        }
        if (command === 'node_location') {
          if (args.operation === 'permissions') return { supported:true,providerAvailable:true,foreground:true,precise:true,background:true,servicesEnabled:true };
          if (args.operation === 'current') return { latest: window.fixture.unavailable ? null : { latitude:-23.5501, longitude:-46.6301, accuracyMeters:window.fixture.accuracy, capturedAt:new Date(Date.now()-window.fixture.age*1000).toISOString(), ageSeconds:window.fixture.age } };
        }
        return {};
      }};
    });
    const page = await context.newPage(); activePage=page; const errors = []; page.on('pageerror', error => {errors.push(error.message);report.pageErrors.push(error.stack ?? error.message); console.error(error.stack ?? error.message);});
    await page.goto(`${base}/__places_browser__`); await page.evaluate(scale => document.documentElement.style.fontSize = `${16*scale}px`, fontScale);
    const panel = page.getByRole('dialog', { name: 'Configurações', exact: true }); await panel.waitFor();
    const last = panel.locator('article').last();
    await last.getByRole('button',{name:'Editar',exact:true}).scrollIntoViewIfNeeded();
    assert.ok(await panel.locator('.settings-content').evaluate(el=>el.scrollTop)>1000, 'Real edit begins in a scrolled long list');
    await last.getByRole('button',{name:'Editar',exact:true}).click();
    const name = panel.getByLabel('Nome',{exact:true}); await name.waitFor();
    assert.equal(await name.evaluate(el=>document.activeElement===el),true,'Editor immediately receives focus');
    const nameBounds = await name.boundingBox(); assert.ok(nameBounds.y>=0 && nameBounds.y+nameBounds.height<844,'Editor is in the viewport after bottom-list click');
    await page.evaluate(()=>window.dispatchEvent(new Event('aegis-back')));
    await panel.locator('article').last().waitFor();
    assert.equal(await panel.getByRole('alertdialog').count(),0,'Opening and Android back without changes never prompts');
    assert.equal(await panel.locator('[data-edit-place="place-29"]').evaluate(el=>document.activeElement===el),true,'Returns to the edited list item');
    await panel.locator('[data-edit-place="place-29"]').click();
    await name.fill('Nome temporário'); await name.fill('Lugar 29');
    await panel.getByLabel('Raio em metros',{exact:true}).first().fill('6');
    await page.evaluate(()=>window.dispatchEvent(new Event('aegis-back')));
    await panel.getByRole('alertdialog',{name:'Descartar alterações do lugar?'}).waitFor();
    await panel.getByRole('button',{name:'Continuar editando',exact:true}).click();
    await panel.getByLabel('Raio em metros',{exact:true}).first().fill('65');
    await panel.getByRole('button',{name:'Minha localização',exact:true}).click();
    await panel.getByText(/Precisão estimada: 13 m/).waitFor();
    await panel.locator('[aria-label="Posição do celular"]').waitFor();
    await panel.getByText('Mapa de fundo indisponível. Você ainda pode ajustar os círculos e seus raios.',{exact:true}).waitFor();
    assert.equal(await panel.getByText('Não foi possível abrir o mapa. Seus lugares salvos continuam disponíveis.',{exact:true}).count(),0,'Existing-area fitBounds must not fail map initialization');
    assert.equal(await panel.locator('.leaflet-place-current-position-pane').evaluate(el=>getComputedStyle(el).zIndex),'650','Current marker stays above coincident numbered geometry');
    assert.equal(await panel.getByLabel('Raio em metros',{exact:true}).count(),2);
    assert.equal(await panel.getByLabel('Raio em metros',{exact:true}).first().inputValue(),'65','Current location never changes geometry');
    const tools = panel.locator('.place-map-tools'); const map = panel.locator('.place-map');
    if(width===320 && fontScale===1){await tools.evaluate(el=>el.scrollIntoView({block:'start'}));await page.screenshot({path:join(screenshotDir,'places-320-tools-and-position.png')});}
    assert.equal(await tools.evaluate(el=>el.nextElementSibling?.classList.contains('place-map-editor')),true,'Search/current controls immediately precede the map component');
    await tools.getByLabel('Buscar endereço',{exact:true}).fill('Rua sintética');
    await tools.getByRole('button',{name:'Buscar endereço',exact:true}).click();
    await tools.getByRole('button',{name:/Endereço sintético/}).click();
    await tools.getByText(/Endereço mostrado no mapa/).waitFor();
    assert.equal(await panel.getByLabel('Raio em metros',{exact:true}).first().inputValue(),'65');
    await panel.getByRole('button',{name:'Cancelar',exact:true}).click();
    await panel.locator('article').last().waitFor();
    assert.equal(await panel.getByRole('alertdialog').count(),0,'Search, position and viewport changes are not persisted changes');
    await panel.locator('[data-edit-place="place-29"]').click();
    await page.evaluate(()=>window.fixture.accuracy=100);
    await panel.getByRole('button',{name:'Minha localização',exact:true}).click();
    await panel.getByText(/Localização imprecisa para esta área/).waitFor();
    await panel.getByRole('button',{name:'Avaliar área de monitoramento maior',exact:true}).click();
    await panel.getByText(/O aviso de saída pode chegar mais longe/).waitFor();
    assert.equal(await panel.getByLabel('Raio em metros',{exact:true}).first().inputValue(),'65','Reviewing a larger area does not apply it');
    await panel.getByRole('button',{name:'Manter minha área',exact:true}).click();
    for (const accuracy of [30,65,200,201]) { await page.evaluate(value=>window.fixture.accuracy=value,accuracy); await panel.getByRole('button',{name:'Minha localização',exact:true}).click(); await panel.getByText(new RegExp(`Precisão estimada: ${accuracy} m`)).waitFor(); }
    await panel.getByText(/Localização imprecisa: acima de 200 m/).waitFor();
    await page.evaluate(()=>window.fixture.age=600);
    await panel.getByRole('button',{name:'Minha localização',exact:true}).click();
    await panel.getByText(/Última posição disponível, de 10 min atrás/).waitFor();
    await page.evaluate(()=>window.fixture.unavailable=true);
    await panel.getByRole('button',{name:'Minha localização',exact:true}).click();
    await panel.getByText(/Localização indisponível\. Confira/).waitFor();
    await page.evaluate(()=>window.fixture.searchFailure=true);
    await tools.getByLabel('Buscar endereço',{exact:true}).fill('Endereço'); await tools.getByRole('button',{name:'Buscar endereço',exact:true}).click();
    await tools.getByText('A busca não está disponível agora. Tente novamente.',{exact:true}).waitFor();
    assert.equal(await panel.getByText(/raw-private-error/).count(),0);
    const noOverflow = await panel.locator('.settings-content').evaluate(el=>el.scrollWidth <= el.clientWidth+1); assert.equal(noOverflow,true,`No overflow ${width}px font ${fontScale}`);
    await name.fill('Lugar alterado');
    await page.setViewportSize({width,height:500}); // Mobile keyboard reduces the visual area.
    await panel.getByRole('button',{name:'Cancelar',exact:true}).click();
    await panel.getByRole('alertdialog',{name:'Descartar alterações do lugar?'}).waitFor();
    await panel.getByRole('button',{name:'Continuar editando',exact:true}).click();
    await page.evaluate(()=>window.dispatchEvent(new Event('aegis-back')));
    await panel.getByRole('alertdialog',{name:'Descartar alterações do lugar?'}).waitFor();
    await panel.getByRole('button',{name:'Continuar editando',exact:true}).click();
    await panel.getByRole('button',{name:'Fechar configurações',exact:true}).click();
    await panel.getByRole('alertdialog',{name:'Descartar alterações?',exact:true}).waitFor();
    await panel.getByRole('button',{name:'Continuar editando',exact:true}).click();
    await page.evaluate(()=>window.fixture.conflict=true);
    await panel.getByRole('button',{name:'Salvar lugar',exact:true}).click();
    await panel.getByText(/Se o lugar mudou em outro dispositivo/).waitFor();
    await assertInViewport(panel.locator('[data-place-error]'),panel.locator('.settings-content'));
    assert.equal(await name.inputValue(),'Lugar alterado','Revision conflict retains entered values');
    assert.equal(await panel.getByText(/internal-private-detail/).count(),0);
    await page.evaluate(()=>{window.fixture.conflict=false;window.fixture.saveDelay=600;window.fixture.failNextList=true;});
    await panel.getByRole('button',{name:'Salvar lugar',exact:true}).click();
    assert.equal(await name.evaluate(el=>Boolean(el.closest('[inert]'))),true,'Inputs and map are inert while the captured save is in flight');
    await page.keyboard.type('não pode alterar');
    assert.equal(await name.inputValue(),'Lugar alterado','Typing during save cannot introduce an unsent change');
    await page.evaluate(()=>window.dispatchEvent(new Event('aegis-back')));
    assert.equal(await panel.getByRole('alertdialog').count(),0,'Android back during save must not offer discard of an in-flight write');
    await panel.getByText('Lugar salvo; não foi possível atualizar a lista. Atualize quando a conexão voltar.',{exact:true}).waitFor();
    await page.evaluate(()=>window.fixture.saveDelay=0);
    assert.equal(await page.evaluate(()=>window.fixture.saved.at(-1).circles[0].radiusMeters),65);
    assert.equal(await panel.locator('[data-edit-place="place-29"]').evaluate(el=>document.activeElement===el),true);
    await page.setViewportSize({width,height:844});
    await panel.locator('[data-edit-place="place-29"]').click();
    await page.evaluate(()=>{window.fixture.unavailable=false;window.fixture.age=0;window.fixture.accuracy=100;});
    await panel.getByRole('button',{name:'Minha localização',exact:true}).click();
    await panel.getByRole('button',{name:'Avaliar área de monitoramento maior',exact:true}).click();
    await panel.getByRole('button',{name:'Usar área de monitoramento de 150 m',exact:true}).click();
    assert.equal(await panel.getByLabel('Raio em metros',{exact:true}).first().inputValue(),'150');
    assert.equal(await panel.getByLabel('Conferi a área que representa este lugar.').isChecked(),false,'Explicit geometry change requires fresh confirmation');
    await panel.getByRole('button',{name:'Salvar lugar',exact:true}).click();
    await panel.getByText('Confirme a área escolhida no mapa antes de salvar.',{exact:true}).waitFor();
    await assertInViewport(panel.locator('[data-place-error]'),panel.locator('.settings-content'));
    await panel.getByLabel('Conferi a área que representa este lugar.').check();
    if(screenshotDir) { await map.scrollIntoViewIfNeeded(); await page.screenshot({path:`${screenshotDir}/places-${width}-font-${fontScale}.png`}); }
    await panel.getByRole('button',{name:'Salvar lugar',exact:true}).click(); await panel.getByText('Lugar salvo.',{exact:true}).waitFor();
    assert.equal(await page.evaluate(()=>window.fixture.saved.at(-1).circles[0].radiusMeters),150);
    await panel.locator('[data-edit-place="place-29"]').click();
    await name.fill('Descartar este nome'); await panel.getByRole('button',{name:'Cancelar',exact:true}).click(); await panel.getByRole('button',{name:'Descartar alterações',exact:true}).click();
    await panel.locator('article').filter({hasText:'Lugar alterado'}).waitFor();
    await panel.getByRole('button',{name:'Novo lugar',exact:true}).click();
    await name.fill('Novo lugar explícito');
    await panel.getByRole('button',{name:'Minha localização',exact:true}).click();
    assert.equal(await panel.getByLabel('Raio em metros',{exact:true}).count(),0,'My location cannot define geometry for a new place');
    await panel.getByRole('button',{name:'Adicionar no centro do mapa',exact:true}).click();
    assert.equal(await panel.getByLabel('Raio em metros',{exact:true}).inputValue(),'150');
    await panel.getByLabel('Raio em metros',{exact:true}).fill('65'); await panel.getByLabel('Raio em metros',{exact:true}).blur();
    await panel.getByRole('button',{name:'Adicionar círculo no mapa',exact:true}).click(); await panel.locator('.place-map').click({position:{x:60,y:140}});
    assert.equal(await panel.getByLabel('Raio em metros',{exact:true}).count(),2,'Actual map click adds a second circle');
    await panel.getByLabel('Conferi a área que representa este lugar.').check();
    await page.evaluate(()=>window.fixture.failNextList=true);
    await panel.getByRole('button',{name:'Salvar lugar',exact:true}).click(); await panel.getByText('Lugar salvo; não foi possível atualizar a lista. Atualize quando a conexão voltar.',{exact:true}).waitFor();
    assert.equal(await page.evaluate(()=>window.fixture.saved.at(-1).circles.length),2);
    assert.equal(await panel.getByLabel('Nome',{exact:true}).count(),0,'Accepted create closes the editor even if refreshing the list fails');
    assert.equal(await page.evaluate(()=>window.fixture.saved.filter(item=>item.name==='Novo lugar explícito').length),1,'Refresh failure cannot ask the user to submit a duplicate create');
    await panel.getByRole('button',{name:'Fechar configurações',exact:true}).click(); await panel.waitFor({state:'hidden'});
    report.cases.push({width,fontScale,status:'pass',checks:['scrolled edit focus and return','view-only current position and search','distinct position and geometry markers','accuracy13/30/65/100/200/201','stale and unavailable','search failure safe feedback','reviewed explicit area enlargement and confirmation','dirty close/back/cancel/revert','revision conflict preserves values','save feedback and list return','actual map click adds second circle','no horizontal overflow','reduced viewport keyboard simulation']});
    assert.deepEqual(errors,[]); console.log(`Places ${width}px, font ${fontScale}: real scrolled clicks, map/marker/search, accuracy/stale/unavailable, explicit area, save/conflict/cancel/dirty/back/keyboard PASS (native IPC fixture)`);
    await context.close();
  }
} catch(error) {report.failures.push(String(error));if(activePage&&!activePage.isClosed())await activePage.screenshot({path:join(screenshotDir,'failure.png')}).catch(()=>{});throw error;} finally { writeFileSync(join(screenshotDir,'report.json'),JSON.stringify(report,null,2)+'\n');if(browser) await browser.close(); await server.close(); }
