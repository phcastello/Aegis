import assert from 'node:assert/strict';

// Only the disposable API35 emulator's own cache marker can be acknowledged.
export function idleSnapshotAckPath(nonce) {
  assert.match(nonce, /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/);
  return `/data/user/0/com.aegis.node/cache/aegis-idle-snapshot-${nonce}.ack`;
}

export async function captureIdleBoundary(event, snapshot, acknowledge) {
  const value = await snapshot();
  if (event.snapshotNonce !== undefined) {
    assert.equal(event.event, 'end');
    assert.equal(event.phase, 'background');
    const marker = idleSnapshotAckPath(event.snapshotNonce);
    assert(value.metrics !== null, 'Final traffic counters must exist before acknowledgement');
    assert(value.cpu.available && value.cpu.processes.some(process => process.kind === 'app'),
      'Final app CPU snapshot must exist before acknowledgement');
    await acknowledge(marker);
  }
  return value;
}
