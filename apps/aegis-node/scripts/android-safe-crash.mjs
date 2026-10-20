// Test-only crash evidence: discard messages, frames, paths and process/device identifiers.
export function safeAndroidCrashTypes(logcat) {
  const types = new Set();
  for (const line of logcat.split('\n')) {
    const exception = line.match(/AndroidRuntime\s*:\s*(?:Caused by:\s*)?(?:java|javax|android|androidx|kotlin|com)(?:\.[A-Za-z_$][A-Za-z0-9_$]*)*\.([A-Za-z][A-Za-z0-9_]{0,63}(?:Exception|Error))(?::|\s*$)/);
    if (exception) types.add(exception[1]);
    const pending = line.match(/\bPending exception (?:java|javax|android|kotlin|com)(?:\.[A-Za-z_$][A-Za-z0-9_$]*)*\.([A-Za-z][A-Za-z0-9_]{0,63}(?:Exception|Error))(?::|\s*$)/);
    if (pending) types.add(pending[1]);
    if (line.includes('JNI DETECTED ERROR IN APPLICATION')) types.add('JniAbort');
    const signal = line.match(/\bFatal signal (?:6 \(SIGABRT\)|11 \(SIGSEGV\)|7 \(SIGBUS\)|4 \(SIGILL\))/);
    if (signal) types.add(signal[0].match(/SIG[A-Z]+/)[0]);
  }
  return [...types].slice(0, 8);
}

const processCode = name => /^com\.aegis\.node(?::[A-Za-z0-9_.:-]+)?$/.test(name)?'target':
  /^com\.google\.android\.gms(?:[.:][A-Za-z0-9_.:-]+)?$/.test(name)?'gms':
  /^com\.android\.settings(?::[A-Za-z0-9_.:-]+)?$/.test(name)?'settings':
  /^com\.android\.systemui(?::[A-Za-z0-9_.:-]+)?$/.test(name)?'systemui':
  /^(?:com\.android\.launcher[23]?|com\.google\.android\.apps\.nexuslauncher)(?::[A-Za-z0-9_.:-]+)?$/.test(name)?'launcher':
  /^(?:com\.(?:android|google\.android)\.(?:packageinstaller|permissioncontroller))(?::[A-Za-z0-9_.:-]+)?$/.test(name)?'permissioncontroller':
  /^(?:com\.google\.android\.gsf|com\.google\.process\.gapps)(?::[A-Za-z0-9_.:-]+)?$/.test(name)?'googleframework':
  /^com\.google\.android\.location(?::[A-Za-z0-9_.:-]+)?$/.test(name)?'googlelocation':
  /^(?:system_server|android)$/.test(name)?'system':'other';

// Associate exception types with Process records internally. PID/name/messages
// never leave this function; unclassified origins remain explicitly unknown.
export function safeAndroidCrashSummaries(logcat) {
  const origins=new Map(),summaries=new Map();let unkeyed='unknown';
  for(const line of logcat.split('\n')) {
    const pid=line.match(/^\d\d-\d\d\s+\d\d:\d\d:\d\d(?:\.\d+)?\s+(\d+)\s+\d+\s+[VDIWEF]\s+/)?.[1];
    const runtime=line.match(/AndroidRuntime\s*:\s*(.*)$/)?.[1];
    if(runtime?.startsWith('FATAL EXCEPTION:')) {
      if(pid)origins.set(pid,'unknown');else unkeyed='unknown';
    }
    const process=runtime?.match(/^Process:\s*([^,\s]+),\s*PID:\s*(\d+)\s*$/);
    if(process) {
      const code=processCode(process[1]);
      if(!pid || pid===process[2])origins.set(process[2],code);
      if(!pid)unkeyed=code;
    }
    if(origins.size>64)origins.delete(origins.keys().next().value);
    for(const errorType of safeAndroidCrashTypes(line)) {
      const code=pid?(origins.get(pid)??'unknown'):unkeyed;
      const key=`${code}:${errorType}`;
      if(summaries.size<16 || summaries.has(key))summaries.set(key,{processCode:code,errorType});
    }
  }
  return [...summaries.values()];
}
