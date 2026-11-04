import type { ChatMessage, LocalChatMessage } from '../types/chat';

/** One in-flight read per conversation; another invalidation supersedes its snapshot. */
type PendingConversation = { version: number; busy: boolean; dirty: boolean };
export class ConversationSyncQueue<T> {
  private entries = new Map<string, PendingConversation>();
  private flushing: Promise<void> | undefined;
  private flushRequested = false;
  private disposed = false;
  private epoch = 0;
  constructor(private readonly read: (id: string) => Promise<T>,
    private readonly apply: (id: string, value: T) => boolean,
    private readonly blocked: (id: string) => boolean = () => false) {}
  invalidate(id: string): void {
    if (this.disposed) return;
    const row = this.entries.get(id) ?? { version: 0, busy: false, dirty: true };
    row.version++; row.dirty = true; this.entries.set(id, row);
  }
  pending(id: string): boolean { return this.entries.has(id); }
  flush(): Promise<void> {
    if (this.flushing) { this.flushRequested = true; return Promise.resolve(); }
    const epoch = this.epoch;
    const attempted = new Map<PendingConversation, number>();
    const worker = async (): Promise<void> => {
      while (!this.disposed && epoch === this.epoch) {
        const next = [...this.entries].find(([id, row]) => !row.busy && row.dirty && !this.blocked(id) && attempted.get(row) !== row.version);
        if (!next) return;
        const [id, row] = next;
        row.busy = true;
        try {
          while (!this.disposed && epoch === this.epoch && row.dirty && !this.blocked(id)) {
            const version = row.version;
            attempted.set(row, version);
            let value: T;
            try { value = await this.read(id); }
            catch { break; } // Retain invalidation for bounded retries/online/resume.
            if (this.disposed || epoch !== this.epoch) return;
            if (version !== row.version) continue;
            if (this.blocked(id)) break;
            if (!this.apply(id, value)) continue;
            row.dirty = false;
          }
        } finally { row.busy = false; if (!row.dirty && this.entries.get(id) === row) this.entries.delete(id); }
      }
    };
    // Replay can contain 128 destinations after a long suspension. Bound HTTP reads.
    this.flushing = Promise.all([worker(), worker(), worker()]).then(() => undefined).finally(() => {
      this.flushing = undefined;
      if (this.flushRequested) { this.flushRequested = false; void this.flush(); }
    });
    return this.flushing;
  }
  reset(): void { this.epoch++; this.entries.clear(); }
  dispose(): void { this.disposed = true; this.reset(); }
}

/** Keep local objects/keys, including interrupted messages, while deduplicating server IDs. */
export function mergeConversationMessages(local: LocalChatMessage[], incoming: ChatMessage[]): LocalChatMessage[] {
  const byId = new Map(local.map(message => [message.serverId ?? message.id, message]));
  const used = new Set<LocalChatMessage>();
  const serverPositions = new Map(incoming.map((message, index) => [message.id, index]));
  // The stream has no user ID. Match accepted local users in order between known
  // server-message anchors, including an accepted turn interrupted before onDone.
  const acceptedUsers = new Map<string, LocalChatMessage>();
  let lowerBound = -1;
  for (let index = 0; index < local.length; index++) {
    const user = local[index];
    const known = serverPositions.get(user.serverId ?? user.id);
    if (known !== undefined) { lowerBound = Math.max(lowerBound, known); continue; }
    if (user.role !== 'user' || user.serverId || user.pending) continue;
    const nextAnchor = local.slice(index + 1).map(message => serverPositions.get(message.serverId ?? message.id))
      .find(position => position !== undefined && position > lowerBound);
    const upperBound = nextAnchor ?? incoming.length;
    const candidate = incoming.findIndex((message, position) => position > lowerBound && position < upperBound &&
      message.role === 'user' && message.content === user.content && !acceptedUsers.has(message.id));
    if (candidate >= 0) { acceptedUsers.set(incoming[candidate].id, user); lowerBound = candidate; }
  }
  const merged = incoming.map(message => {
    const existing = byId.get(message.id) ?? acceptedUsers.get(message.id);
    if (!existing) return { ...message, serverId: message.id };
    used.add(existing);
    if (!existing.pending && !existing.streaming) Object.assign(existing, message, { id: existing.id, serverId: message.id });
    return existing;
  });
  // Keep interrupted/unacknowledged rows beside their original local neighbors.
  for (let index = 0; index < local.length; index++) {
    const message = local[index];
    if (used.has(message) || serverPositions.has(message.serverId ?? message.id)) continue;
    let insertion = -1;
    for (let previous = index - 1; previous >= 0; previous--) {
      const position = merged.indexOf(local[previous]);
      if (position >= 0) { insertion = position + 1; break; }
    }
    if (insertion < 0) for (let next = index + 1; next < local.length; next++) {
      const position = merged.indexOf(local[next]);
      if (position >= 0) { insertion = position; break; }
    }
    merged.splice(insertion < 0 ? merged.length : insertion, 0, message);
  }
  return merged;
}
