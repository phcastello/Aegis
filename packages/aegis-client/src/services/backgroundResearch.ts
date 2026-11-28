import { requestJson } from './aegisApi';
import type { WebResearchSourceCount } from '../types/chat';

export interface ResearchJob {
  id: string;
  conversationId: string;
  userMessageId: string;
  state: 'queued' | 'running' | 'completed' | 'failed' | 'cancelled';
  phase: 'queued' | 'searching' | 'executing' | 'finishing' | 'finished' | null;
  kind?: 'python' | 'video' | null;
  createdAt: string;
  updatedAt: string;
  sources: WebResearchSourceCount[];
  resultMessageId?: string | null;
  errorCode?: string | null;
}

export function isResearchPending(job: ResearchJob): boolean {
  return job.state === 'queued' || job.state === 'running';
}

export function listResearchJobs(conversationId: string, signal?: AbortSignal): Promise<ResearchJob[]> {
  return requestJson('/api/chat/research-jobs?conversationId=' + encodeURIComponent(conversationId), { cache: 'no-store', signal });
}

export function cancelResearchJob(id: string): Promise<ResearchJob> {
  return requestJson('/api/chat/research-jobs/' + encodeURIComponent(id), { method: 'DELETE' });
}
