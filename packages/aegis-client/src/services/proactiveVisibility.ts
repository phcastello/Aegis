// Read the current layout, never an IntersectionObserver snapshot from an older
// conversation. Checking the final strip also works for messages taller than the
// viewport and avoids exact intersection ratios on fractional WebView pixels.
export function isMessageEndVisible(element: HTMLElement, container: HTMLElement): boolean {
  if (!element.isConnected || !container.contains(element) || element.getClientRects().length === 0) return false;
  const bounds = element.getBoundingClientRect();
  const root = container.getBoundingClientRect();
  const viewport = window.visualViewport;
  const left = Math.max(root.left + container.clientLeft, viewport?.offsetLeft ?? 0);
  const top = Math.max(root.top + container.clientTop, viewport?.offsetTop ?? 0);
  const right = Math.min(root.left + container.clientLeft + container.clientWidth,
    (viewport?.offsetLeft ?? 0) + (viewport?.width ?? window.innerWidth));
  const bottom = Math.min(root.top + container.clientTop + container.clientHeight,
    (viewport?.offsetTop ?? 0) + (viewport?.height ?? window.innerHeight));
  const strip = Math.min(8, bounds.height);
  if (strip <= 0 || bounds.bottom > bottom + 0.5 || bounds.bottom - strip < top ||
      bounds.right <= left || bounds.left >= right) return false;
  const x = (Math.max(left, bounds.left) + Math.min(right, bounds.right)) / 2;
  const y = bounds.bottom - strip / 2;
  const hit = document.elementFromPoint(x, y);
  return hit !== null && element.contains(hit);
}
