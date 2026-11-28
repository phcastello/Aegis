import { clientEnvironment, type SharedAttachment } from './clientEnvironment';
import { requestJson } from './aegisApi';

export const MAX_ATTACHMENT_COUNT = 20;
export const MAX_ATTACHMENT_BYTES = 512 * 1024 * 1024;
const MAX_CHUNK_BYTES = 4 * 1024 * 1024;
const UPLOAD_LIFETIME = 24 * 60 * 60 * 1000;

interface Upload { id: string; chunkSize: number; next: number; expiresAt: number }
let nextDraftId = 0;
interface DraftAttachmentInfo {
  localId: number;
  name: string;
  size: number;
  type: string;
}
export interface DraftFile extends DraftAttachmentInfo {
  kind: 'file';
  source: File | string;
  previewUrl?: string;
  upload?: Upload;
}
export interface DraftLink extends DraftAttachmentInfo { kind: 'link'; url: string }
export type DraftAttachment = DraftFile | DraftLink;

export function pickedFile(source: File): DraftFile {
  return { kind: 'file', localId: ++nextDraftId, source, name: source.name, size: source.size, type: source.type || 'application/octet-stream' };
}

export function sharedAttachmentFile(file: SharedAttachment): DraftFile {
  const previewUrl = file.previewUrl?.startsWith('data:image/jpeg;base64,') ? file.previewUrl : undefined;
  return { kind: 'file', localId: ++nextDraftId, source: file.handle, name: file.fileName, size: file.sizeBytes, type: file.contentType, previewUrl };
}

export function attachmentLinkUrl(value: string): URL | null {
  const text = value.trim();
  if (text.length > 2048 || !/^https?:\/\//i.test(text) || /[\s\\\x00-\x1f\x7f]/.test(text)) return null;
  try {
    const url = new URL(text);
    return (url.protocol === 'http:' || url.protocol === 'https:') && !url.username && !url.password &&
      url.host && url.href.length <= 2048 ? url : null;
  } catch { return null; }
}

export function draftLink(value: string): DraftLink | null {
  const url = attachmentLinkUrl(value);
  return url ? { kind: 'link', localId: ++nextDraftId, name: url.hostname, size: url.href.length,
    type: 'text/uri-list', url: url.href } : null;
}

export function checkAttachmentLimits(files: readonly DraftAttachment[]): void {
  if (files.length > MAX_ATTACHMENT_COUNT) throw new Error(`Adicione até ${MAX_ATTACHMENT_COUNT} anexos por mensagem.`);
  for (const file of files) {
    if (file.kind === 'link') continue;
    if (!Number.isSafeInteger(file.size) || file.size < 1) throw new Error(`O arquivo ${file.name} está vazio ou não pôde ser lido.`);
    if (file.size > MAX_ATTACHMENT_BYTES) throw new Error(`${file.name} excede 512 MiB por arquivo.`);
  }
}

function ensureActive(signal: AbortSignal): void {
  if (signal.aborted) throw new DOMException('Envio interrompido.', 'AbortError');
}

const uploadPath = (id: string) => `/api/chat/uploads/${encodeURIComponent(id)}`;

export function chatAttachmentUrl(messageId: string, attachmentId: string): string {
  const baseUrl = clientEnvironment().apiBaseUrl;
  return `${baseUrl}/api/chat/messages/${encodeURIComponent(messageId)}/attachments/${encodeURIComponent(attachmentId)}`;
}

async function startUpload(file: DraftFile, signal: AbortSignal): Promise<Upload> {
  const expiresAt = Date.now() + UPLOAD_LIFETIME;
  const info = await requestJson<{ id: string; chunkSize: number; sizeBytes: number }>('/api/chat/uploads', {
    method: 'POST', signal, redirect: 'error',
    body: JSON.stringify({ fileName: file.name, contentType: file.type, sizeBytes: file.size })
  });
  ensureActive(signal);
  if (typeof info?.id !== 'string' || !info.id || info.sizeBytes !== file.size ||
      !Number.isSafeInteger(info.chunkSize) || info.chunkSize < 1 || info.chunkSize > MAX_CHUNK_BYTES)
    throw new Error('O servidor devolveu uma sessão de envio inválida.');
  return { id: info.id, chunkSize: info.chunkSize, next: 0, expiresAt };
}

async function putChunk(file: DraftFile, upload: Upload, signal: AbortSignal): Promise<number> {
  const env = clientEnvironment();
  if (typeof file.source === 'string') {
    if (!env.uploadSharedChunk) throw new Error('Compartilhamento de arquivo indisponível.');
    return env.uploadSharedChunk(file.source, upload.id, upload.next, upload.chunkSize, signal);
  }
  const offset = upload.next * upload.chunkSize;
  const response = await fetch(`${env.apiBaseUrl}${uploadPath(upload.id)}/chunks/${upload.next}`, {
    method: 'PUT', signal, redirect: 'error',
    headers: { 'Content-Type': 'application/octet-stream' }, body: file.source.slice(offset, offset + upload.chunkSize)
  });
  return response.status;
}

export async function uploadAttachment(file: DraftFile, signal: AbortSignal, progress: (bytes: number) => void): Promise<string> {
  ensureActive(signal);
  if (!file.upload || Date.now() >= file.upload.expiresAt) file.upload = await startUpload(file, signal);
  let upload = file.upload;
  let renewed = false;
  progress(Math.min(upload.next * upload.chunkSize, file.size));
  while (upload.next * upload.chunkSize < file.size) {
    ensureActive(signal);
    const status = await putChunk(file, upload, signal);
    ensureActive(signal);
    if ((status === 404 || status === 410) && !renewed) {
      file.upload = upload = await startUpload(file, signal);
      renewed = true;
      progress(0);
      continue;
    }
    if (status !== 204) throw new Error(`Não foi possível enviar ${file.name} (HTTP ${status}). Tente novamente.`);
    upload.next++;
    progress(Math.min(upload.next * upload.chunkSize, file.size));
  }
  return upload.id;
}

export function releaseAttachment(file: DraftAttachment, discardUpload = false): void {
  if (file.kind === 'link') return;
  if (typeof file.source === 'string') void clientEnvironment().releaseSharedAttachment?.(file.source).catch(() => {});
  if (discardUpload && file.upload)
    void requestJson<void>(uploadPath(file.upload.id), { method: 'DELETE', redirect: 'error' }).catch(() => {});
}

export function attachmentSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.ceil(bytes / 1024)} KiB`;
  return `${(bytes / (1024 * 1024)).toLocaleString('pt-BR', { maximumFractionDigits: 1 })} MiB`;
}
