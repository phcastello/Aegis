export type EmailConnectionWaitResult = 'connected' | 'timeout' | 'cancelled';

export async function waitForEmailConnection(
  isConnected: (signal: AbortSignal) => Promise<boolean>,
  signal: AbortSignal,
  intervalMs = 750,
  timeoutMs = 12000
): Promise<EmailConnectionWaitResult> {
  if (signal.aborted) return 'cancelled';

  const controller = new AbortController();
  const abort = (): void => controller.abort();
  signal.addEventListener('abort', abort, { once: true });
  const deadline = globalThis.setTimeout(abort, timeoutMs);

  try {
    while (!controller.signal.aborted) {
      try {
        if (await isConnected(controller.signal) && !controller.signal.aborted) {
          return 'connected';
        }
      } catch {
        // A temporary status error is retried until the deadline.
      }

      if (controller.signal.aborted) break;
      await new Promise<void>((resolve) => {
        const timer = globalThis.setTimeout(() => {
          controller.signal.removeEventListener('abort', onAbort);
          resolve();
        }, intervalMs);
        const onAbort = (): void => {
          globalThis.clearTimeout(timer);
          resolve();
        };
        controller.signal.addEventListener('abort', onAbort, { once: true });
      });
    }

    return signal.aborted ? 'cancelled' : 'timeout';
  } finally {
    globalThis.clearTimeout(deadline);
    signal.removeEventListener('abort', abort);
  }
}
