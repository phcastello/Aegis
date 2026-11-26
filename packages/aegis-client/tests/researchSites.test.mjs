import assert from 'node:assert/strict';
import test from 'node:test';
import { loadTypeScript } from './loadTypeScript.mjs';

const { isUsableResearchFavicon, researchFavicon, researchSite } = await loadTypeScript('../src/services/researchSites.ts');
const canonicalHost = 'agenciabrasil.ebc.com.br';
const iconRoute = '/api/chat/research-favicon?host=';

test('all public site icons use the same Aegis endpoint and canonical host query', () => {
  assert.deepEqual(researchSite(canonicalHost), { host: canonicalHost, name: 'Agência Brasil' });
  assert.deepEqual(researchSite(`https://www.${canonicalHost}/noticia`), researchSite(canonicalHost));
  assert.equal(researchFavicon(canonicalHost), iconRoute + canonicalHost);
  assert.equal(researchFavicon(`www.${canonicalHost}`), iconRoute + canonicalHost);

  const forged = `${canonicalHost}.example.org`;
  assert.equal(researchSite(forged).name, 'Example');
  assert.equal(researchFavicon(forged), iconRoute + forged);
  assert.equal(researchFavicon(`${canonicalHost}@example.org`), undefined);
  assert.equal(researchFavicon('example.test'), undefined);
  assert.equal(researchFavicon('127.0.0.1'), undefined);
});

test('unknown sites get a compact label and retain the actual domain for their icon', () => {
  const url = 'https://noticias.portal-ciencia.com.br/artigo?edicao=2026';
  assert.deepEqual(researchSite(url), { host: 'noticias.portal-ciencia.com.br', name: 'Portal Ciencia' });
  assert.equal(researchSite(url, 'Portal Ciência').name, 'Portal Ciência');
  assert.equal(researchSite(url, url).name, 'Portal Ciencia');
  assert.equal(researchSite(url, 'noticias.portal-ciencia.com.br').name, 'Portal Ciencia');
});

test('pending, broken and one-pixel icons do not replace the monogram', () => {
  assert.equal(isUsableResearchFavicon({ complete: false, naturalWidth: 16, naturalHeight: 16 }), false);
  assert.equal(isUsableResearchFavicon({ complete: true, naturalWidth: 0, naturalHeight: 0 }), false);
  assert.equal(isUsableResearchFavicon({ complete: true, naturalWidth: 1, naturalHeight: 1 }), false);
  assert.equal(isUsableResearchFavicon({ complete: true, naturalWidth: 16, naturalHeight: 16 }), true);
});
