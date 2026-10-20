import type { DeliveryPolicy, RecurrenceRule } from './domains.js';

export interface ReminderEditorFields {
  kind: 'one' | 'series' | 'geographic'; text: string; due: string; timezone: string; policy: DeliveryPolicy;
  rule: RecurrenceRule; ending: 'never' | 'date' | 'count'; untilDate: string; count: number;
  placeIds: string[]; observer: string; transition: 'enter' | 'exit'; repeat: boolean; starts: string; ends: string;
}

// Compare the fields that the current form will save. Hidden defaults and the
// order of a place selection do not create a false unsaved-change warning.
export function reminderEditorSnapshot(fields: ReminderEditorFields): string {
  const common = { kind: fields.kind, text: fields.text, timezone: fields.timezone };
  if (fields.kind === 'one') return JSON.stringify({ ...common, due: fields.due, policy: fields.policy });
  if (fields.kind === 'geographic') return JSON.stringify({ ...common, placeIds: [...fields.placeIds].sort(), observer: fields.observer, transition: fields.transition, repeat: fields.repeat, starts: fields.starts, ends: fields.ends });
  const { frequency, interval, localTime, startDate, weekdays, monthDay, lastDay } = fields.rule;
  return JSON.stringify({ ...common, policy: fields.policy, rule: { frequency, interval, localTime, startDate,
    ...(frequency === 'weekly' ? { weekdays: [...(weekdays ?? [])].sort() } : {}),
    ...(frequency === 'monthly' ? lastDay ? { lastDay: true } : { monthDay } : {}),
    ...(fields.ending === 'date' ? { untilDate: fields.untilDate } : fields.ending === 'count' ? { count: fields.count } : {}),
  } });
}
