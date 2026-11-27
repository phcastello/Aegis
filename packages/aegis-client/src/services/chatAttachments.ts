import type { AttachmentInput } from '../types/chat';

export const MAX_ATTACHMENT_COUNT = 4;
export const MAX_ATTACHMENT_BYTES = 8 * 1024 * 1024;

export function checkAttachmentLimits(files: readonly File[]): void {
  if (files.length > MAX_ATTACHMENT_COUNT) throw new Error('Anexe até quatro arquivos por mensagem.');
  if (files.some(file => !file.size)) throw new Error('O arquivo selecionado está vazio.');
  if (files.reduce((sum, file) => sum + file.size, 0) > MAX_ATTACHMENT_BYTES)
    throw new Error('Os anexos de uma mensagem podem somar até 8 MiB.');
}

export function encodeAttachment(file: File): Promise<AttachmentInput> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const url = String(reader.result);
      resolve({ fileName: file.name, contentType: file.type || 'application/octet-stream', dataBase64: url.slice(url.indexOf(',') + 1) });
    };
    reader.onerror = () => reject(new Error(`Não foi possível ler ${file.name}. Selecione o arquivo novamente.`));
    reader.readAsDataURL(file);
  });
}

export function sharedAttachmentFile(input: AttachmentInput): File {
  const binary = atob(input.dataBase64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return new File([bytes], input.fileName, { type: input.contentType });
}

export function attachmentSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.ceil(bytes / 1024)} KiB`;
  return `${(bytes / (1024 * 1024)).toLocaleString('pt-BR', { maximumFractionDigits: 1 })} MiB`;
}
