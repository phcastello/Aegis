import type { WebResearchSourceCount } from '../types/chat';

/** Presentation only. Reveal actual received text in short increments, independently
 * of transport chunk size. No audio/request backpressure and no fabricated text. */
export const STREAM_FRAME_MS = 32;
export class StreamTextReveal {
  private target = '';
  private offset = 0;
  private deadline = 0;
  get content() { return this.target; }
  get visible() { return this.target.slice(0, this.offset); }
  get pending() { return this.offset < this.target.length; }

  append(text: string, now: number): void {
    if (!text) return;
    this.target += text;
    // A fast stream can arrive in a single burst. Preserve a fluid cadence but
    // bound the tail to 1.8s after the final incoming piece, even for long answers.
    this.deadline = now + Math.min(1800, Math.max(160, (this.target.length - this.offset) * 10));
  }

  step(now: number): string {
    if (!this.pending) return this.visible;
    const left = this.target.length - this.offset;
    const budget = Math.max(1, Math.ceil(left * STREAM_FRAME_MS / Math.max(STREAM_FRAME_MS, this.deadline - now + STREAM_FRAME_MS)));
    let end = Math.min(this.target.length, this.offset + budget);
    // Never render a broken UTF-16 surrogate pair in the middle of an emoji.
    if (end < this.target.length && this.target.charCodeAt(end - 1) >= 0xd800 && this.target.charCodeAt(end - 1) <= 0xdbff) end++;
    this.offset = end;
    return this.visible;
  }

  finish(): string { this.offset = this.target.length; return this.visible; }
}

const uuid = (value: unknown): value is string => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
export type NativeVoiceStreamKind = 'conversation' | 'final_segment' | 'done' | 'interrupted' | 'error' | 'ignored' | 'tool_status' | 'web_research_sources';
export interface NativeResearchStatus {
  category: 'web_search';
  state: 'started' | 'completed' | 'failed';
  message: string;
}
export interface NativeVoiceStreamEvent {
  type: NativeVoiceStreamKind;
  turnId: string;
  conversationId: string | null;
  assistantMessageId: string | null;
  segmentId?: string;
  sequence?: number;
  content?: string;
  toolStatus?: NativeResearchStatus;
  sources?: WebResearchSourceCount[];
}

export function parseWebResearchSourceCounts(value: unknown, maxItems = 8): WebResearchSourceCount[] {
  if (!Array.isArray(value) || value.length > maxItems) return [];
  const sources = new Map<string, WebResearchSourceCount>();
  for (const source of value) {
    if (!source || typeof source !== 'object') continue;
    const { url, count } = source as Record<string, unknown>;
    if (typeof url !== 'string' || url.length > 1024 || !/^https?:\/\//i.test(url) ||
      /[\s\\\u0000-\u001f\u007f]/.test(url) || typeof count !== 'number' ||
      !Number.isInteger(count) || count < 1 || count > 2_147_483_647) continue;
    try {
      const parsed = new URL(url);
      if (!parsed.hostname || parsed.username || parsed.password || sources.has(parsed.href)) continue;
      sources.set(parsed.href, { url: parsed.href, count });
    } catch { /* An invalid source cannot affect the chat stream. */ }
  }
  return [...sources.values()];
}

export function parseNativeVoiceStreamEvent(payload: unknown): NativeVoiceStreamEvent | null {
  if (!payload || typeof payload !== 'object') return null;
  const row = payload as Record<string, unknown>;
  if (!uuid(row.turnId) || typeof row.type !== 'string' || !['conversation', 'final_segment', 'done', 'interrupted', 'error', 'ignored', 'tool_status', 'web_research_sources'].includes(row.type)) return null;
  if (row.conversationId != null && !uuid(row.conversationId) || row.assistantMessageId != null && !uuid(row.assistantMessageId)) return null;
  const conversationId = uuid(row.conversationId) ? row.conversationId : null;
  const assistantMessageId = uuid(row.assistantMessageId) ? row.assistantMessageId : null;
  const type = row.type as NativeVoiceStreamKind;
  if (type === 'conversation' && !conversationId || type === 'done' && (!conversationId || !assistantMessageId)) return null;
  if (type === 'tool_status') {
    if (!conversationId || assistantMessageId !== null || !row.toolStatus || typeof row.toolStatus !== 'object') return null;
    const status = row.toolStatus as Record<string, unknown>;
    if (status.category !== 'web_search' || (status.state !== 'started' && status.state !== 'completed' && status.state !== 'failed') ||
      typeof status.message !== 'string' || !status.message.trim() || status.message.length > 240) return null;
    return { type, turnId: row.turnId, conversationId, assistantMessageId,
      toolStatus: { category: 'web_search', state: status.state, message: status.message } };
  }
  if (type === 'web_research_sources') {
    if (!conversationId || assistantMessageId !== null) return null;
    const sources = parseWebResearchSourceCounts(row.sources);
    return sources.length ? { type, turnId: row.turnId, conversationId, assistantMessageId, sources } : null;
  }
  if (type === 'final_segment') {
    if (!conversationId || !assistantMessageId || !uuid(row.segmentId) || typeof row.sequence !== 'number' ||
      !Number.isInteger(row.sequence) || row.sequence < 0 || row.sequence >= 128 || typeof row.content !== 'string' ||
      !row.content.trim() || row.content.length > 16000) return null;
    return { type, turnId: row.turnId, conversationId, assistantMessageId, segmentId: row.segmentId, sequence: row.sequence, content: row.content };
  }
  return { type, turnId: row.turnId, conversationId, assistantMessageId };
}

/** Only committed, consecutive speech segments of one assistant message may be
 * displayed. A missed/invalid event falls back to authoritative conversation sync. */
export class NativeVoiceSegments {
  private ids = new Map<string, { sequence: number; content: string }>();
  private sequence = 0;
  private assistant: string | null = null;
  private units = 0;
  constructor(readonly turnId: string, readonly conversationId: string) {}
  get assistantMessageId() { return this.assistant; }
  matches(event: NativeVoiceStreamEvent) {
    return event.turnId === this.turnId && (!event.conversationId || event.conversationId === this.conversationId) &&
      (!event.assistantMessageId || !this.assistant || event.assistantMessageId === this.assistant);
  }
  append(event: NativeVoiceStreamEvent): string | null {
    if (!this.matches(event) || event.type !== 'final_segment' || event.sequence === undefined || !event.segmentId ||
        !event.content || !event.assistantMessageId) return null;
    const prior = this.ids.get(event.segmentId);
    if (prior) return prior.sequence === event.sequence && prior.content === event.content ? '' : null;
    if (event.sequence !== this.sequence || this.units + event.content.length > 1024 * 1024) return null;
    const delta = this.sequence ? ' ' + event.content : event.content;
    this.assistant = event.assistantMessageId;
    this.ids.set(event.segmentId, { sequence: event.sequence, content: event.content }); this.sequence++; this.units += delta.length;
    return delta;
  }
}
