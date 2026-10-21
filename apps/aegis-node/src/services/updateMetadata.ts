// The publisher emits one immutable installer URL per version. Reject transport/target drift.
export function validateWindowsUpdate(metadata: Record<string, unknown>, version: string): void {
  // This endpoint is stable even though its historical URL says node-preview.
  if (!/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(version) || metadata.version !== version)
    throw new Error('Unexpected Windows updater version or channel.');
  const platforms = metadata.platforms as Record<string, { url?: unknown }> | undefined;
  const url = platforms?.['windows-x86_64']?.url;
  const expected = `https://github.com/phcastello/Aegis/releases/download/node-v${version}/Aegis-Windows-x86_64-Setup.exe`;
  if (url !== expected) throw new Error('Unexpected Windows updater URL.');
}
