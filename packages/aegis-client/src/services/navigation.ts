import { clientEnvironment } from './clientEnvironment';
const layers: { close: () => void }[] = [];
export async function dismissKeyboard(): Promise<void> {
  if (document.activeElement instanceof HTMLElement && document.activeElement.matches('input, textarea, [contenteditable="true"]')) document.activeElement.blur();
  await clientEnvironment().dismissKeyboard?.().catch(() => undefined);
}
export function hasNavigationLayer(): boolean { return layers.length > 0; }
export function navigationLayer(close: () => void): () => void {
  const entry = { close };
  layers.push(entry);
  const changed = () => window.dispatchEvent(new Event('aegis-navigation-layer'));
  changed();
  return () => { const index = layers.indexOf(entry); if (index >= 0) layers.splice(index, 1); changed(); };
}
window.addEventListener('aegis-back', event => {
  const layer = layers.at(-1);
  if (!layer || document.querySelector('.settings-dialog[open]')) return;
  event.preventDefault(); event.stopImmediatePropagation(); layer.close();
}, true);
