import type { MemoryActivityKind, MemoryActivitySection } from '../types/chat';

export const memoryActivityLabels: Record<MemoryActivityKind, { title: string; section: string }> = {
  used: { title: 'Usou memória', section: 'Usou' },
  consulted: { title: 'Consultou memória', section: 'Consultou' },
  created: { title: 'Guardou memória', section: 'Guardou' },
  updated: { title: 'Atualizou memória', section: 'Atualizou' },
  deleted: { title: 'Apagou memória', section: 'Apagou' }
};

const order: MemoryActivityKind[] = ['used', 'consulted', 'created', 'updated', 'deleted'];

export function orderedMemorySections(sections: MemoryActivitySection[]): MemoryActivitySection[] {
  return order.flatMap((kind) => sections.filter((section) => section.kind === kind && section.totalCount > 0));
}

export function memoryActivityTitle(sections: MemoryActivitySection[]): string {
  return sections.length === 1 ? memoryActivityLabels[sections[0]!.kind].title : 'Memória';
}

export function visibleMemoryItems(items: string[]): string[] {
  return [...new Set(items)].slice(0, 3);
}

export function remainingMemoryItems(totalCount: number, items: string[]): number {
  return Math.max(0, totalCount - visibleMemoryItems(items).length);
}
