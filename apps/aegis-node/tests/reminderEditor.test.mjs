import assert from 'node:assert/strict';
import { test } from 'node:test';
import { reminderEditorSnapshot } from '../.test-build/services/reminderEditor.js';

const form = { kind:'geographic', text:'Ao sair', due:'2026-10-20T08:00', timezone:'America/Sao_Paulo', policy:'allEligible', rule:{frequency:'daily',interval:1,localTime:'08:00',startDate:'2026-10-20',weekdays:[1],monthDay:1,lastDay:false}, ending:'never', untilDate:'', count:10, placeIds:['one','two'], observer:'phone', transition:'exit', repeat:false, starts:'', ends:'' };
test('geographic dirty state follows saved fields and clears when values are reverted', () => {
  const initial=reminderEditorSnapshot(form);
  assert.equal(initial,reminderEditorSnapshot({...form,placeIds:['two','one'],due:'different hidden default',rule:{...form.rule,interval:8}}));
  for(const change of [{text:'Changed'},{placeIds:['one']},{observer:'other'},{transition:'enter'},{repeat:true},{starts:'2026-11-01T08:00'},{ends:'2026-11-10T08:00'}])assert.notEqual(initial,reminderEditorSnapshot({...form,...change}));
  assert.equal(initial,reminderEditorSnapshot(structuredClone(form)));
});
test('single and recurring dirty snapshots include editable schedule and delivery fields', () => {
  const one={...form,kind:'one'};
  assert.notEqual(reminderEditorSnapshot(one),reminderEditorSnapshot({...one,due:'2026-10-21T08:00'}));
  assert.equal(reminderEditorSnapshot(one),reminderEditorSnapshot({...one,observer:'hidden'}));
  const weekly={...form,kind:'series',rule:{...form.rule,frequency:'weekly',weekdays:[1,3]}};
  assert.equal(reminderEditorSnapshot(weekly),reminderEditorSnapshot({...weekly,rule:{...weekly.rule,weekdays:[3,1]}}));
  assert.notEqual(reminderEditorSnapshot(weekly),reminderEditorSnapshot({...weekly,rule:{...weekly.rule,weekdays:[1]}}));
  assert.notEqual(reminderEditorSnapshot(weekly),reminderEditorSnapshot({...weekly,ending:'count'}));
  assert.notEqual(reminderEditorSnapshot(weekly),reminderEditorSnapshot({...weekly,policy:'bestAvailable'}));
});
