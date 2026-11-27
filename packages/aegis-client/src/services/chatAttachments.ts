import { clientEnvironment, type SharedAttachment } from './clientEnvironment';
import { requestJson } from './aegisApi';

export const MAX_ATTACHMENT_COUNT = 4;
export const MAX_ATTACHMENT_BYTES = 512 * 1024 * 1024;
const MAX_CHUNK_BYTES = 4 * 1024 * 1024;
const UPLOAD_LIFETIME = 24 * 60 * 60 * 1000;

interface Upload { id: string; chunkSize: number; next: number; expiresAt: number }
let nextDraftId = 0;
export interface DraftAttachment {
  localId: number;
  name: string;
  size: number;
  type: string;
  source: File | string;
  previewUrl?: string;
  upload?: Upload;
}

export function pickedFile(source: File): DraftAttachment {
  return { localId: ++nextDraftId, source, name: source.name, size: source.size, type: source.type || 'application/octet-stream' };
}

export function sharedAttachmentFile(file: SharedAttachment): DraftAttachment {
  const previewUrl = file.previewUrl?.startsWith('data:image/jpeg;base64,') ? file.previewUrl : undefined;
  return { localId: ++nextDraftId, source: file.handle, name: file.fileName, size: file.sizeBytes, type: file.contentType, previewUrl };
}

export function checkAttachmentLimits(files: readonly DraftAttachment[]): void {
  if (files.length > MAX_ATTACHMENT_COUNT) throw new Error('Anexe até quatro arquivos por mensagem.');
  for (const file of files) {
    if (!Number.isSafeInteger(file.size) || file.size < 1) throw new Error(`O arquivo ${file.name} está vazio ou não pôde ser lido.`);
    if (file.size > MAX_ATTACHMENT_BYTES) throw new Error(`${file.name} excede 512 MiB por arquivo.`);
  }
}

function ensureActive(signal: AbortSignal): void {
  if (signal.aborted) throw new DOMException('Envio interrompido.', 'AbortError');
}

const uploadPath = (id: string) => `/api/chat/uploads/${encodeURIComponent(id)}`;

async function startUpload(file: DraftAttachment, signal: AbortSignal): Promise<Upload> {
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

async function putChunk(file: DraftAttachment, upload: Upload, signal: AbortSignal): Promise<number> {
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

export async function uploadAttachment(file: DraftAttachment, signal: AbortSignal, progress: (bytes: number) => void): Promise<string> {
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
  if (typeof file.source === 'string') void clientEnvironment().releaseSharedAttachment?.(file.source).catch(() => {});
  if (discardUpload && file.upload)
    void requestJson<void>(uploadPath(file.upload.id), { method: 'DELETE', redirect: 'error' }).catch(() => {});
}

export function attachmentSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.ceil(bytes / 1024)} KiB`;
  return `${(bytes / (1024 * 1024)).toLocaleString('pt-BR', { maximumFractionDigits: 1 })} MiB`;
}
