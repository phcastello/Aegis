import { computed, onBeforeUnmount, onMounted, ref, type Ref } from 'vue';

/** Touch-only drawer drag. Native vertical and nested horizontal scrolling keep priority. */
export function useSidebarGesture(surface: Ref<HTMLElement | null>, open: Ref<boolean>, drawer: MediaQueryList) {
  const progress = ref<number | null>(null);
  const dragging = computed(() => progress.value !== null);
  const style = computed(() => dragging.value ? { '--sidebar-progress': String(progress.value) } : {});
  let tracking: {
    id: number; startX: number; startY: number; lastX: number; lastTime: number;
    width: number; initiallyOpen: boolean; velocity: number;
  } | null = null;
  let suppressClickUntil = 0;

  function reset(): void {
    tracking = null;
    progress.value = null;
  }

  function start(event: TouchEvent): void {
    reset();
    // A fresh tap is intentional; suppress only the click synthesized from the drag.
    suppressClickUntil = 0;
    if (!drawer.matches || event.touches.length !== 1 || !(event.target instanceof Element)) return;
    // Text entry and scrollable code/tables must retain their own gestures.
    // Links and history buttons keep taps; a locked drag suppresses their click.
    if (event.target.closest('input, textarea, select, form, [contenteditable="true"], .conversation-info')) return;
    for (let element: Element | null = event.target; element && element !== surface.value; element = element.parentElement) {
      if (element.scrollWidth > element.clientWidth && /auto|scroll/.test(getComputedStyle(element).overflowX)) return;
    }
    const sidebar = surface.value?.querySelector<HTMLElement>('#conversation-sidebar');
    if (!sidebar) return;
    const touch = event.touches[0]!;
    tracking = {
      id: touch.identifier, startX: touch.clientX, startY: touch.clientY,
      lastX: touch.clientX, lastTime: event.timeStamp, velocity: 0,
      width: sidebar.getBoundingClientRect().width, initiallyOpen: open.value
    };
  }

  function move(event: TouchEvent): void {
    if (!tracking) return;
    if (event.touches.length !== 1 || !drawer.matches) { reset(); return; }
    const touch = Array.from(event.touches).find(item => item.identifier === tracking?.id);
    if (!touch) { reset(); return; }
    const dx = touch.clientX - tracking.startX;
    const dy = touch.clientY - tracking.startY;
    if (!dragging.value) {
      if (Math.max(Math.abs(dx), Math.abs(dy)) < 8) return;
      if (Math.abs(dy) > Math.abs(dx) * 1.2) { reset(); return; }
      if (Math.abs(dx) <= Math.abs(dy) * 1.2) return;
      if ((tracking.initiallyOpen && dx >= 0) || (!tracking.initiallyOpen && dx <= 0) || !event.cancelable) {
        reset(); return;
      }
    }
    // Non-passive only after the horizontal direction lock; vertical scroll stays native.
    if (event.cancelable) event.preventDefault();
    const elapsed = event.timeStamp - tracking.lastTime;
    if (elapsed > 0) tracking.velocity = (touch.clientX - tracking.lastX) / elapsed;
    tracking.lastX = touch.clientX;
    tracking.lastTime = event.timeStamp;
    progress.value = Math.max(0, Math.min(1, (tracking.initiallyOpen ? 1 : 0) + dx / tracking.width));
  }

  function end(event: TouchEvent): void {
    if (!tracking) return;
    if (dragging.value) {
      const distance = tracking.lastX - tracking.startX;
      const velocity = event.timeStamp - tracking.lastTime < 100 ? tracking.velocity : 0;
      const flick = Math.abs(distance) >= 24 && Math.abs(velocity) >= 0.5;
      open.value = flick ? velocity > 0 : tracking.initiallyOpen ? progress.value! > 0.65 : progress.value! >= 0.35;
      suppressClickUntil = performance.now() + 350;
    }
    reset();
  }

  function suppressDragClick(event: MouseEvent): void {
    if (event.detail !== 0 && performance.now() < suppressClickUntil) {
      event.preventDefault();
      event.stopImmediatePropagation();
    }
  }

  onMounted(() => {
    surface.value?.addEventListener('touchstart', start, { passive: true });
    surface.value?.addEventListener('touchmove', move, { passive: false });
    surface.value?.addEventListener('touchend', end);
    surface.value?.addEventListener('touchcancel', reset);
    surface.value?.addEventListener('click', suppressDragClick, true);
    window.addEventListener('resize', reset);
    window.addEventListener('orientationchange', reset);
    drawer.addEventListener('change', reset);
  });
  onBeforeUnmount(() => {
    surface.value?.removeEventListener('touchstart', start);
    surface.value?.removeEventListener('touchmove', move);
    surface.value?.removeEventListener('touchend', end);
    surface.value?.removeEventListener('touchcancel', reset);
    surface.value?.removeEventListener('click', suppressDragClick, true);
    window.removeEventListener('resize', reset);
    window.removeEventListener('orientationchange', reset);
    drawer.removeEventListener('change', reset);
  });

  return { dragging, style };
}
