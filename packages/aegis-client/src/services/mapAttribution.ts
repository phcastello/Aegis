import DOMPurify from 'dompurify';
export function sanitizeMapAttribution(value: string): string {
  return DOMPurify.sanitize(value, { ALLOWED_TAGS: ['a'], ALLOWED_ATTR: ['href', 'title', 'target', 'rel'] });
}
