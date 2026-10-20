// Disposable Android framework harness only. Never expose adb args/output in errors.
const operationFor = args => {
  if(args[0]==='shell') {
    if(args[1]==='am')return args[2]==='instrument'?'instrumentation':args[2]==='force-stop'?'force_stop':'activity_manager';
    return ({pm:'package_manager',settings:'settings',getprop:'boot_state',dumpsys:'package_state',service:'notification_permission',cat:'restore_state',input:'unlock',id:'root_verification'})[args[1]]??'shell';
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
    args[0]==='root' && /cannot run as root|root access is disabled|root is not allowed/i.test(stderr)?'root_denied':
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
export async function ensureAndroidFrameworkRoot(run,{timeoutMs=60000,now=Date.now,sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms))}={}) {
  const deadline=now()+timeoutMs;
  const remaining=()=>{
    const value=deadline-now();
    if(value<=0)throw new AdbOperationError('root_bootstrap','timeout');
    return value;
  };
  const bounded=(args,timeout=5000)=>run(args,Math.min(timeout,remaining()));
  const ready=()=>waitForAndroidFrameworkReady(bounded,{timeoutMs:remaining(),now,sleep});
  const verified=async()=>{
    while(true) {
      try {
        const uid=bounded(['shell','id','-u']).trim();
        if(!/^\d+$/.test(uid))throw new AdbOperationError('root_verification','invalid_response');
        return uid==='0';
      }catch(error) {
        if(!(error instanceof AdbOperationError) || error.reason!=='connection_unavailable')throw error;
        await sleep(Math.min(1000,remaining()));
        await ready();
      }
    }
  };
  await ready();
  if(await verified())return;
  for(let attempt=0;attempt<2;attempt++) {
    try {
      const reply=bounded(['root']);
      if(/cannot run as root|root access is disabled|root is not allowed/i.test(reply))throw new AdbOperationError('root','root_denied');
    }catch(error) {
      // adbd may apply root and restart while its command reports a closed link.
      if(!(error instanceof AdbOperationError) || error.reason!=='connection_unavailable')throw error;
    }
    await ready();
    if(await verified())return;
  }
  throw new AdbOperationError('root_verification','root_not_applied');
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
