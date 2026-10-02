import type { MemoryActivitySnapshot } from '../types/chat';

export type MemoryActivityPollResult = 'completed' | 'timeout' | 'cancelled';

export async function pollMemoryActivity(
  read: (signal: AbortSignal) => Promise<MemoryActivitySnapshot>,
  update: (snapshot: MemoryActivitySnapshot) => void,
  signal: AbortSignal,
  isCurrent: () => boolean,
  intervalMs = 1000,
  timeoutMs = 20_000
): Promise<MemoryActivityPollResult> {
  if (signal.aborted || !isCurrent()) return 'cancelled';
  const controller = new AbortController();
  const abort = (): void => controller.abort();
  signal.addEventListener('abort', abort, { once: true });
  const timeout = globalThis.setTimeout(abort, timeoutMs);
  const deadline = Date.now() + timeoutMs;
  const active = (): boolean => !controller.signal.aborted && isCurrent();
  const stopped = (): MemoryActivityPollResult => signal.aborted || !isCurrent() ? 'cancelled' : 'timeout';
  try {
    while (active() && Date.now() < deadline) {
      await new Promise<void>((resolve) => {
        const timer = globalThis.setTimeout(() => {
          controller.signal.removeEventListener('abort', onAbort);
          resolve();
        }, Math.min(intervalMs, Math.max(0, deadline - Date.now())));
        const onAbort = (): void => { globalThis.clearTimeout(timer); resolve(); };
        controller.signal.addEventListener('abort', onAbort, { once: true });
        if (controller.signal.aborted) onAbort();
      });
      if (!active()) return stopped();
      if (Date.now() >= deadline) break;
      try {
        const snapshot = await read(controller.signal);
        if (!active()) return stopped();
        update(snapshot);
        if (!snapshot.pending) return 'completed';
      } catch {
        if (!active()) return stopped();
      }
    }
    return active() ? 'timeout' : stopped();
  } finally {
    globalThis.clearTimeout(timeout);
    signal.removeEventListener('abort', abort);
  }
}
