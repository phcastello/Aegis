import type { ChatAttachment, LocalChatMessage } from '../types/chat';

export interface VideoCitation {
  messageId: string;
  attachmentId: string;
  seconds: number;
}

const idPattern = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
const referencePattern = new RegExp(`^aegis-video:(${idPattern})/(${idPattern})#t=(0|[1-9][0-9]{0,8})$`, 'i');

export function parseVideoCitation(reference: string): VideoCitation | null {
  const match = referencePattern.exec(reference);
  return match ? { messageId: match[1].toLowerCase(), attachmentId: match[2].toLowerCase(), seconds: Number(match[3]) } : null;
}

export function resolveVideoCitation(messages: readonly LocalChatMessage[], citation: VideoCitation): ChatAttachment | undefined {
  const message = messages.find(item => (item.serverId ?? item.id).toLowerCase() === citation.messageId);
  const file = message?.attachments?.find(item => item.id.toLowerCase() === citation.attachmentId);
  if (!file) return;
  const mime = file.contentType.toLowerCase();
  if (['video/mp4', 'video/quicktime', 'video/webm', 'video/x-matroska'].includes(mime) ||
      (mime === 'application/octet-stream' && /\.(mp4|mov|webm|mkv)$/i.test(file.fileName))) return file;
}

export function formatVideoTime(seconds: number): string {
  const minutes = Math.floor(seconds / 60);
  const twoDigits = (value: number) => String(value).padStart(2, '0');
  const short = `${twoDigits(minutes % 60)}:${twoDigits(seconds % 60)}`;
  return minutes < 60 ? short : `${Math.floor(minutes / 60)}:${short}`;
}

export function copyVideoCitationLabels(content: string): string {
  return content.replace(/\[([^\]\r\n]+)\]\((aegis-video:[^\s)]+)\)/gi,
    (original, label, ref) => parseVideoCitation(ref) ? label : original);
}
