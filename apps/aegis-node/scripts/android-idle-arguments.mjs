// adb shell joins its arguments into a remote shell command. Optional empty
// values must be omitted, otherwise am consumes the next option as their value.
import assert from 'node:assert/strict';

export function idleInstrumentationArguments({mode,pairingCode,expectedNodeId,expectedVersion}) {
  assert(['before','after'].includes(mode),'Idle mode required');
  assert(/^\d+\.\d+\.\d+(?:-[a-z0-9.]+)?$/.test(expectedVersion??''),'Expected native version required');
  const options=['shell','am','instrument','-w','-r','-e','class','com.aegis.node.NodeIdleFrameworkTest','-e','waitForActivitiesToComplete','false','-e','idleMode',mode,'-e','phaseDurationMs','150000','-e','expectedVersion',expectedVersion];
  if(mode==='before') {
    assert(/^\d{6,12}$|^[A-Z0-9-]{4,32}$/i.test(pairingCode??''),'Pairing code required before upgrade');
    options.push('-e','pairingCode',pairingCode);
  } else {
    assert(/^[a-f0-9-]{36}$/.test(expectedNodeId??''),'Existing native identity required after upgrade');
    options.push('-e','expectedNodeId',expectedNodeId);
  }
  return [...options,'com.aegis.node.test/androidx.test.runner.AndroidJUnitRunner'];
}
