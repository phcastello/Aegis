// Disposable Android framework harness only. Never expose adb args/output in errors.
const operationFor = args => {
  if(args[0]==='shell') {
    if(args[1]==='am')return args[2]==='instrument'?'instrumentation':args[2]==='force-stop'?'force_stop':'activity_manager';
    return ({pm:'package_manager',settings:'settings',getprop:'boot_state',dumpsys:'package_state',service:'notification_permission',cat:'restore_state',input:'unlock'})[args[1]]??'shell';
  }
  return ({install:'install',root:'root',reboot:'reboot',reverse:'reverse',logcat:'logcat','wait-for-device':'wait_for_device'})[args[0]]??'other';
};
export class AdbOperationError extends Error {
  constructor(operation,reason) {
    super(`adb_failed operation=${operation} reason=${reason}`);
    this.operation=operation;this.reason=reason;
  }
}
export function adbFailure(args,result) {
  const stderr=typeof result.stderr==='string'?result.stderr:'';
  const reason=result.error?.code==='ETIMEDOUT'?'timeout':result.error?.code==='ENOENT'?'adb_missing':result.error?'spawn_failed':
    /device offline|no devices\/emulators found|device .* not found|device unauthorized|closed|cannot connect to daemon/i.test(stderr)?'connection_unavailable':
    /can't find service|could not access the package manager|package manager has died/i.test(stderr)?'framework_unavailable':'command_failed';
  return new AdbOperationError(operationFor(args),reason);
}
export async function waitForAndroidFrameworkReady(run,{timeoutMs=60000,now=Date.now,sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms))}={}) {
  const deadline=now()+timeoutMs;
  while(true) {
    try {
      const budget=()=>Math.max(1,Math.min(5000,deadline-now()));
      if(run(['shell','getprop','sys.boot_completed'],budget()).trim()==='1' && /^package:/m.test(run(['shell','pm','path','com.aegis.node'],budget())))return;
    }catch{/* Only connection/readiness probes repeat; tests and permissions never do. */}
    if(now()>=deadline)throw new AdbOperationError('framework_readiness','timeout');
    await sleep(Math.min(1000,deadline-now()));
  }
}
export function frameworkCleanup(primaryError,emit=console.log) {
  let failure;
  const operations=new Set(['restore_location','restore_permission','force_stop','validate_checkpoints','safe_diagnostics']);
  return {
    attempt(operation,action) {
      try {action();}catch(error) {
        const safeOperation=operations.has(operation)?operation:'other';
        const reason=error instanceof AdbOperationError?error.reason:'step_failed';
        failure??=Error(`framework_cleanup_failed operation=${safeOperation} reason=${reason}`);
        emit(`phase=framework_cleanup operation=${safeOperation} reason=${reason}`);
      }
    },
    finish() {if(!primaryError && failure)throw failure;}
  };
}
