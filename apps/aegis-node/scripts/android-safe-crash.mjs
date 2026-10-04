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
