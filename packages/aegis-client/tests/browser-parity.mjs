import { chromium } from 'playwright-core';
import assert from 'node:assert/strict';
import http from 'node:http';
const fixture = { id: 'c1', title: 'Conversa de teste', createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), messageCount: 2, messages: [{ id: 'm0', conversationId: 'c1', role: 'assistant', content: 'Histórico preservado.', createdAt: new Date().toISOString() }] };
let cancellations = 0, feedback = 0, deletions = 0;
const json = (res, body, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)); };
const server = http.createServer(async (req, res) => {
 if (req.headers.origin === 'http://127.0.0.1:1420') res.setHeader('Access-Control-Allow-Origin', req.headers.origin);
 res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
 res.setHeader('Access-Control-Allow-Methods', 'GET,POST,DELETE,PATCH,OPTIONS');
 if (req.method === 'OPTIONS') { res.writeHead(204); res.end(); return; }
 const url = new URL(req.url, 'http://localhost');
 if (!url.pathname.startsWith('/api/')) {
   const response = await fetch('http://127.0.0.1:18092' + req.url);
   const headers = Object.fromEntries(response.headers); delete headers['content-encoding']; delete headers['content-length']; delete headers['transfer-encoding'];
   res.writeHead(response.status, headers); res.end(Buffer.from(await response.arrayBuffer())); return;
 }
 let raw = ''; for await (const part of req) raw += part;
 const body = raw ? JSON.parse(raw) : {};
 const path = url.pathname;
 if (path === '/api/health') return json(res, { status: 'ok' });
 if (path === '/api/voice/status') return json(res, { enabled: false, available: false });
 if (path === '/api/voice/transcription/status') return json(res, { enabled: false, configured: false });
 if (path === '/api/email/status') return json(res, { isConnected: true, emailAddress: 'test@example.test' });
 if (path === '/api/notifications/configuration') return json(res, { enabled: false, publicKey: null });
 if (path === '/api/chat/conversations') return json(res, { items: deletions ? [] : [fixture], hasMore: false });
 if (path.endsWith('/title')) { fixture.title = body.title; return json(res, fixture); }
 if (path.endsWith('/feedback')) { feedback++; return json(res, { id: 'f1', rating: body.rating }); }
 if (path.endsWith('/memory-activity')) return json(res, { pending: false, sections: [{ kind: 'used', items: ['Fato de teste.'], totalCount: 1 }] });
 if (path === '/api/chat/conversations/c1' && req.method === 'DELETE') { deletions++; return json(res, null, 204); }
 if (path === '/api/chat/conversations/c1') return json(res, fixture);
 if (path.includes('/turns/') && req.method === 'DELETE') { cancellations++; return json(res, null, 204); }
 if (path.includes('/turns/')) return json(res, null, 204);
 if (path === '/api/chat/messages/stream') {
   res.writeHead(200, { 'Content-Type': 'application/x-ndjson' });
   const event = value => res.write(JSON.stringify({ turnId: body.turnId, ...value }) + '\n');
   event({ type: 'conversation', conversationId: 'c1' });
   event({ type: 'token', content: 'Parcial ' });
   if (body.content === 'cancelar') return;
   event({ type: 'tool_status', category: 'email', state: 'started', message: 'Consultando integração…' });
   setTimeout(() => {
     event({ type: 'token', content: '**completa** [Link](https://example.test/article) [Conectar Google](https://accounts.google.com/o/oauth2/v2/auth?client_id=test)' });
     event({ type: 'done', conversationId: 'c1', messageId: 'm1', conversationTitle: fixture.title, memoryActivity: { pending: false, sections: [{ kind: 'used', items: ['Fato de teste.'], totalCount: 1 }] } });
     res.end();
   }, 1500);
   return;
 }
 json(res, { error: 'Unknown fixture path' }, 404);
});
// The test uses a private in-memory API fixture; it never writes to the real Aegis backend.
for (const url of ['http://127.0.0.1:18092', 'http://127.0.0.1:1420']) {
  const deadline = Date.now() + 15000;
  while (true) {
    try { const response = await fetch(url); if (response.ok) break; } catch {}
    if (Date.now() >= deadline) throw new Error('Start the web preview and native Vite servers before testing: ' + url);
    await new Promise(resolve => setTimeout(resolve, 150));
  }
}
await new Promise(resolve => server.listen(18093, '127.0.0.1', resolve));
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
try {
 for (const host of ['web', 'native']) {
   const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
   await context.addInitScript(() => localStorage.setItem('aegis.voice.autoSpeak', 'false')); // Audio/device acceptance is separate.
   if (host === 'native') await context.addInitScript(() => {
     window.openedUrls = [];
     window.__TAURI_INTERNALS__ = { invoke: async (command, args) => {
       if (command === 'runtime_info') return { platform: 'android', backendUrl: 'http://127.0.0.1:18093', version: '0.7.0-stage.2' };
       if (command === 'check_backend') return { status: 'connected', message: null };
       if (command === 'check_android_update') return null;
       if (command === 'plugin:opener|open_url') { window.openedUrls.push(args.url); return; }
       throw new Error('Unexpected IPC: ' + command);
     } };
   });
   const page = await context.newPage(); const errors = [];
   page.on('pageerror', e => { errors.push(e.message); console.log('PAGEERROR', host, e.message); });
   page.on('console', m => { if (m.type() === 'error') console.log('CONSOLEERROR', host, m.text()); });
   await page.goto(host === 'web' ? 'http://127.0.0.1:18093' : 'http://127.0.0.1:1420');
   await page.getByLabel('Abrir histórico').click();
   await page.getByRole('button', { name: 'Conversa de teste', exact: true }).click();
   await page.getByText('Histórico preservado.', { exact: true }).waitFor();
   await page.locator('textarea').fill('teste'); await page.getByLabel('Enviar mensagem').click();
   await page.getByText('Parcial', { exact: true }).waitFor();
   assert.equal(await page.locator('.markdown-message strong').count(), 0);
   await page.getByText('Consultando integração…', { exact: true }).waitFor();
   await page.locator('.markdown-message strong').waitFor();
   await page.getByText('Usou memória', { exact: true }).click(); await page.getByText('Fato de teste.', { exact: true }).waitFor();
   await page.getByLabel('Marcar resposta como boa').last().click(); await page.getByRole('button', { name: 'Salvar feedback' }).click();
   await page.getByRole('dialog', { name: 'Boa resposta' }).waitFor({ state: 'hidden' });
   await page.getByLabel('Marcar resposta como ruim').last().click();
   await page.getByRole('button', { name: 'Salvar feedback' }).click();
   await page.getByRole('dialog', { name: 'Dar um bonk nessa resposta' }).waitFor({ state: 'hidden' });
   assert.equal(feedback, host === 'web' ? 2 : 4);
   if (host === 'native') {
     await page.getByRole('link', { name: 'Link', exact: true }).click();
     await page.getByRole('link', { name: 'Conectar Google' }).click();
     await page.evaluate(() => window.dispatchEvent(new Event('focus'))); // Return from the external OAuth browser.
     await page.getByText('Conta Google conectada', { exact: false }).waitFor();
     assert.equal((await page.evaluate(() => window.openedUrls)).length, 2);
     assert.equal(await page.evaluate(() => navigator.serviceWorker.getRegistrations().then(r => r.length)), 0);
     await page.getByLabel('Sobre a Aegis').click(); await page.getByText('Android', { exact: true }).waitFor();
     await page.getByText('Connected', { exact: true }).waitFor(); await page.getByRole('button', { name: 'Fechar', exact: true }).click();
   }
   await page.locator('textarea').fill('cancelar'); await page.getByLabel('Enviar mensagem').click();
   await page.getByLabel('Interromper geração').click(); await page.getByLabel('Enviar mensagem').waitFor();
   await page.getByLabel('Abrir histórico').click();
   await page.getByLabel('Ações de Conversa de teste').click(); await page.getByRole('button', { name: 'Renomear', exact: true }).click();
   await page.getByLabel('Renomear conversa').fill('Renomeada'); await page.getByLabel('Renomear conversa').press('Enter');
   await page.getByRole('button', { name: 'Renomeada', exact: true }).waitFor();
   await page.getByRole('button', { name: 'Nova conversa', exact: true }).click();
   await page.setViewportSize({ width: 1600, height: 900 });
   if (process.env.PARITY_SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.PARITY_SCREENSHOT_DIR}/${host}-parity.png` });
   await page.getByLabel('Ações de Renomeada').click();
   await page.getByRole('button', { name: 'Apagar', exact: true }).click();
   await page.getByRole('dialog', { name: 'Apagar conversa' }).getByRole('button', { name: 'Apagar', exact: true }).click();
   await page.getByRole('dialog', { name: 'Apagar conversa' }).waitFor({ state: 'hidden' });
   assert.equal(deletions, 1);
   assert.deepEqual(errors, []);
   console.log(`${host}: UI, streaming before done, cancel, history, rename, Markdown, feedback, memory, mobile/resize PASS (fixture; native IPC mocked)`);
   await context.close(); fixture.title = 'Conversa de teste'; deletions = 0;
 }
 assert.equal(feedback, 4); assert.equal(cancellations, 2);
} finally { await browser.close(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
