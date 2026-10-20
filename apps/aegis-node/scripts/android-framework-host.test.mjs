import {test} from 'node:test';
import assert from 'node:assert/strict';
import {adbFailure,frameworkCleanup,waitForAndroidFrameworkReady,AdbOperationError} from './android-framework-host.mjs';

test('adb failures expose only a closed operation and reason',()=>{
  const secret='private-token /private/device/path fixture-node-id';
  const error=adbFailure(['shell','pm','grant',secret],{status:1,stdout:secret,stderr:`device offline ${secret}`});
  assert.equal(error.message,'adb_failed operation=package_manager reason=connection_unavailable');
  assert(!JSON.stringify(error).includes(secret));assert(!error.stack.includes(secret));
  assert.equal(adbFailure(['shell',secret],{status:null,error:{code:'ETIMEDOUT',message:secret}}).message,'adb_failed operation=shell reason=timeout');
});

test('failed cleanup preserves the primary error and continues safe diagnostics',()=>{
  const primary=Error('original_fixed_test_failure');const emitted=[];let diagnostics=false;
  const cleanup=frameworkCleanup(primary,value=>emitted.push(value));
  cleanup.attempt('restore_permission',()=>{throw new AdbOperationError('package_manager','connection_unavailable');});
  cleanup.attempt('safe_diagnostics',()=>{diagnostics=true;});
  cleanup.attempt('force_stop',()=>{throw new AdbOperationError('force_stop','timeout');});
  assert.doesNotThrow(()=>cleanup.finish());assert(diagnostics);
  assert.deepEqual(emitted,['phase=framework_cleanup operation=restore_permission reason=connection_unavailable','phase=framework_cleanup operation=force_stop reason=timeout']);
  const soleFailure=frameworkCleanup(undefined,()=>{});soleFailure.attempt('force_stop',()=>{throw Error('private cleanup failure');});
  assert.throws(()=>soleFailure.finish(),{message:'framework_cleanup_failed operation=force_stop reason=step_failed'});
});

test('post-root readiness waits for boot and package manager without replaying tests',async()=>{
  let clock=0;let packages=0;const operations=[];
  await waitForAndroidFrameworkReady(args=>{
    operations.push(args.slice(0,3).join(':'));
    if(args[1]==='getprop')return '1\n';
    if(++packages===1)throw new AdbOperationError('package_manager','framework_unavailable');
    return 'package:/private/test/apk/path\n';
  },{timeoutMs:5000,now:()=>clock,sleep:async ms=>{clock+=ms;}});
  assert.equal(packages,2);assert.equal(clock,1000);
  assert(operations.every(operation=>operation==='shell:getprop:sys.boot_completed'||operation==='shell:pm:path'));
});

test('readiness timeout stays bounded and does not disclose package output',async()=>{
  let clock=0;
  await assert.rejects(waitForAndroidFrameworkReady(()=>{throw Error('secret stdout/stderr');},{timeoutMs:2000,now:()=>clock,sleep:async ms=>{clock+=ms;}}),{message:'adb_failed operation=framework_readiness reason=timeout'});
  assert.equal(clock,2000);
});
