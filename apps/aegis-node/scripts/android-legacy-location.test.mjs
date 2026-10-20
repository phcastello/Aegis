import {test} from 'node:test';
import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {join} from 'node:path';
import {captureLegacyLocation,enableLegacyLocation,restoreLegacyLocation,safeLegacyLocation} from './android-legacy-location.mjs';

const candidates=process.platform==='win32'
  ? [process.env.ProgramFiles && join(process.env.ProgramFiles,'Git','bin','sh.exe'),'C:\\Program Files\\Git\\bin\\sh.exe','sh'].filter(Boolean)
  : ['sh'];
const posixShell=candidates.find(candidate=>spawnSync(candidate,['-c',"printf shell_ready"],{encoding:'utf8'}).stdout==='shell_ready');
if(process.platform!=='win32')assert(posixShell,'The mandatory Linux shell parsing fixture requires sh');
const shellTest={skip:posixShell?false:'Windows has no POSIX shell or Git sh; Linux executes this shell parsing fixture'};

// Model SettingsProvider's +/- provider-list contract. Mode writes deliberately
// do not change providers, matching the pre-P shell command's missing Java wrapper.
function settings(initial) {
  const values=new Map(Object.entries(initial)),updates=[];
  const run=args=>{
    const [,command,operation,,name,encoded]=args;
    assert.equal(command,'settings');
    if(operation==='get')return `${values.has(name)?values.get(name):'null'}\n`;
    if(operation==='delete'){if(name!=='location_providers_allowed')values.delete(name);return '';}
    assert.equal(operation,'put');
    // Exercise actual POSIX parsing, including empty strings and metacharacters.
    const parsed=spawnSync(posixShell,['-c',`printf '%s' ${encoded}`],{encoding:'utf8'});
    assert.equal(parsed.status,0);const value=parsed.stdout;updates.push([name,value]);
    if(name==='location_providers_allowed' && /^[+-]/.test(value)) {
      const providers=new Set((values.get(name)??'').split(',').filter(Boolean));
      if(value[0]==='+')providers.add(value.slice(1));else providers.delete(value.slice(1));
      values.set(name,[...providers].join(','));
    } else if(name!=='location_providers_allowed')values.set(name,value);
    return '';
  };
  return {run,values,updates};
}

test('legacy setup changes actual providers NLP first and restores the exact initial list',shellTest,()=>{
  const fixture=settings({location_mode:'1',location_providers_allowed:'gps'});
  const before=captureLegacyLocation(fixture.run);
  enableLegacyLocation(fixture.run);
  assert.deepEqual(fixture.updates.slice(1),[['location_providers_allowed','+network'],['location_providers_allowed','+gps']]);
  assert.deepEqual(safeLegacyLocation(fixture.run),{mode:3,gpsAllowed:true,networkAllowed:true});
  restoreLegacyLocation(fixture.run,before);
  assert.deepEqual(Object.fromEntries(fixture.values),{location_mode:'1',location_providers_allowed:'gps'});
});

test('cleanup preserves a missing mode and an explicitly empty providers value',shellTest,()=>{
  const fixture=settings({location_providers_allowed:''});const before=captureLegacyLocation(fixture.run);
  enableLegacyLocation(fixture.run);restoreLegacyLocation(fixture.run,before);
  assert.equal(fixture.values.has('location_mode'),false);
  assert.equal(fixture.values.has('location_providers_allowed'),true);
  assert.equal(fixture.values.get('location_providers_allowed'),'');
});

test('cleanup quotes scalar values and diagnostic output exposes only known masks',shellTest,()=>{
  const original="gps,network,custom'$(printf expanded)`printf expanded`";
  const mode="3'$(printf expanded)`printf expanded`";
  const fixture=settings({location_mode:mode,location_providers_allowed:original});
  const before=captureLegacyLocation(fixture.run);enableLegacyLocation(fixture.run);restoreLegacyLocation(fixture.run,before);
  assert.equal(fixture.values.get('location_providers_allowed'),original);
  assert.equal(fixture.values.get('location_mode'),mode);
  assert.deepEqual(safeLegacyLocation(fixture.run),{mode:'unknown',gpsAllowed:true,networkAllowed:true});
});

test('a missing providers key is refused before mutation because the legacy API cannot delete it',()=>{
  const fixture=settings({});const before=captureLegacyLocation(fixture.run);
  assert.throws(()=>enableLegacyLocation(fixture.run,before),/legacy_provider_setting_missing/);
  assert.equal(fixture.updates.length,0);
  restoreLegacyLocation(fixture.run,before);
  assert.deepEqual(Object.fromEntries(fixture.values),{});
});
