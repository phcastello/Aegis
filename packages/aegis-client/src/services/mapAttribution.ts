// Copyright (C) 2026 Pedro Hasson Castello.
// GNU AGPL v3 only; attribution terms: /ADDITIONAL_TERMS.md.
import DOMPurify from 'dompurify';
export function sanitizeMapAttribution(value: string): string {
  return DOMPurify.sanitize(value, { ALLOWED_TAGS: ['a'], ALLOWED_ATTR: ['href', 'title', 'target', 'rel'] });
}
