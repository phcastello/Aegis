import {test} from 'node:test';
import assert from 'node:assert/strict';
import {adbFailure,frameworkCleanup,waitForAndroidFrameworkReady,ensureAndroidFrameworkRoot,AdbOperationError} from './android-framework-host.mjs';

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

test('root applied before a closed adb connection is verified without repeating root',async()=>{
  let clock=0,rooted=false,roots=0,disconnects=0,packages=0;
  await ensureAndroidFrameworkRoot(args=>{
    if(args[0]==='root') {
      roots++;rooted=true;disconnects=1;
      throw new AdbOperationError('root','connection_unavailable');
    }
    if(args[1]==='getprop') {
      if(disconnects-->0)throw new AdbOperationError('boot_state','connection_unavailable');
      return '1\n';
    }
    if(args[1]==='pm'){packages++;return 'package:/private/apk/path\n';}
    if(args[1]==='id')return rooted?'0\n':'2000\n';
    assert.fail('Unexpected operation');
  },{timeoutMs:60000,now:()=>clock,sleep:async ms=>{clock+=ms;}});
  assert.equal(roots,1);assert.equal(packages,2);assert.equal(clock,1000);
});

test('root not applied permits at most one extra root command within the shared budget',async()=>{
  for(const appliesOnRetry of [true,false]) {
    let clock=0,roots=0;
    const attempt=ensureAndroidFrameworkRoot(args=>{
      if(args[0]==='root'){roots++;clock+=10000;return 'restarting adbd as root\n';}
      if(args[1]==='getprop')return '1\n';
      if(args[1]==='pm')return 'package:/private/apk/path\n';
      if(args[1]==='id')return appliesOnRetry&&roots===2?'0\n':'2000\n';
      assert.fail('Unexpected operation');
    },{timeoutMs:60000,now:()=>clock,sleep:async ms=>{clock+=ms;}});
    if(appliesOnRetry)await attempt;
    else await assert.rejects(attempt,{message:'adb_failed operation=root_verification reason=root_not_applied'});
    assert.equal(roots,2);assert(clock<=60000);
  }
});

test('permanent root denial fails immediately without a retry or exposing output',async()=>{
  let roots=0;
  await assert.rejects(ensureAndroidFrameworkRoot(args=>{
    if(args[0]==='root'){roots++;return 'adbd cannot run as root in production builds private-output\n';}
    if(args[1]==='getprop')return '1\n';
    if(args[1]==='pm')return 'package:/private/apk/path\n';
    if(args[1]==='id')return '2000\n';
    assert.fail('Unexpected operation');
  }),{message:'adb_failed operation=root reason=root_denied'});
  assert.equal(roots,1);
  assert.equal(adbFailure(['root'],{status:1,stderr:'adbd cannot run as root private-output'}).message,'adb_failed operation=root reason=root_denied');
});

test('root bootstrap shares one sixty-second budget with late readiness and command restart',async()=>{
  let clock=0,roots=0,ready=false;
  await assert.rejects(ensureAndroidFrameworkRoot((args,timeout)=>{
    if(args[1]==='getprop') {
      if(!ready && clock<59000)throw new AdbOperationError('boot_state','connection_unavailable');
      ready=true;return '1\n';
    }
    if(args[1]==='pm')return 'package:/private/apk/path\n';
    if(args[1]==='id')return '2000\n';
    if(args[0]==='root') {
      roots++;assert.equal(timeout,1000);clock+=timeout;
      throw new AdbOperationError('root','connection_unavailable');
    }
    assert.fail('Unexpected operation');
  },{timeoutMs:60000,now:()=>clock,sleep:async ms=>{clock+=ms;}}),{message:'adb_failed operation=root_bootstrap reason=timeout'});
  assert.equal(clock,60000);assert.equal(roots,1);
});
