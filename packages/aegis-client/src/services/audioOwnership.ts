import { clientEnvironment } from './clientEnvironment';

/** The returned lease is idempotent, including late callbacks after cancellation. */
export async function acquireAudio(kind: 'capture' | 'playback'): Promise<() => Promise<void>> {
  const release = await clientEnvironment().acquireAudio?.(kind);
  let released = false;
  return async () => {
    if (released) return;
    released = true;
    await release?.();
  };
}
