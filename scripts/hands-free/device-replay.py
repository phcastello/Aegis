#!/usr/bin/env python3
"""Build the same detector with official NDK and replay one private WAV via ADB.

Explicit diagnostic session only. Uses a new shell-private /data/local/tmp
folder, pulls scores, then removes only its own temporary files. Does not install
an app, reset battery statistics, record ambient audio or change device settings.
Run compare-native-csv.py afterwards on the host with the existing PoC Python.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

REPO=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(REPO/'scripts/wakeword-poc'))
from prepare import private_output
MODEL=json.loads(Path(__file__).with_name('model.json').read_text())


def run(cmd):
    return subprocess.run(list(map(str,cmd)),check=True,capture_output=True,text=True).stdout.strip()


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--ndk',type=Path,required=True)
    p.add_argument('--model',type=Path,required=True)
    p.add_argument('--wav',type=Path,required=True)
    p.add_argument('--serial',required=True,help='Explicit authorized ARM64 device/emulator serial')
    p.add_argument('--adb',default='adb')
    p.add_argument('--cmake',default='cmake')
    p.add_argument('--build-dir',type=private_output,default=Path.home()/'.cache/aegis-hands-free/arm64-replay')
    p.add_argument('--output',type=private_output,required=True)
    args=p.parse_args();os.umask(0o077)
    raw=args.model.read_bytes()
    if len(raw)!=MODEL['bytes'] or hashlib.sha256(raw).hexdigest()!=MODEL['sha256']:raise ValueError('Frozen model hash mismatch')
    # The native executable itself validates the WAV header and format.
    if not args.wav.is_file():raise ValueError('WAV missing')
    toolchain=args.ndk/'build/cmake/android.toolchain.cmake'
    stl=list(args.ndk.glob('toolchains/llvm/prebuilt/*/sysroot/usr/lib/aarch64-linux-android/libc++_shared.so'))
    if not toolchain.is_file() or len(stl)!=1:raise ValueError('Use official host-compatible Android NDK with ARM64 libc++')
    adb=[args.adb,'-s',args.serial]
    abi=run([*adb,'shell','getprop','ro.product.cpu.abi'])
    if abi!='arm64-v8a':raise ValueError('This replay requires an ARM64 device/emulator; no cross-ABI parity claim')
    args.output.mkdir(parents=True,exist_ok=False,mode=0o700)
    args.build_dir.mkdir(parents=True,exist_ok=True,mode=0o700)
    configure=[args.cmake,'-S',REPO/'apps/aegis-node/src-tauri/gen/android/app/src/main/cpp','-B',args.build_dir,'-G','Ninja',
               '-DCMAKE_BUILD_TYPE=Release',f'-DCMAKE_TOOLCHAIN_FILE={toolchain}', '-DANDROID_ABI=arm64-v8a','-DANDROID_PLATFORM=android-24',
               '-DANDROID_STL=c++_shared','-DANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES=ON','-DAEGIS_BUILD_REPLAY=ON',
               f'-DAEGIS_REPLAY_SOURCE={Path(__file__).with_name("replay-native.cpp")}']
    log=run(configure)+'\n'+run([args.cmake,'--build',args.build_dir,'--target','aegis_mww_replay','-j','4'])
    (args.output/'build.log').write_text(log+'\n')
    remote='/data/local/tmp/aegis-parity-'+uuid.uuid4().hex
    files={'aegis_mww_replay':args.build_dir/'aegis_mww_replay','libaegis_microwakeword.so':args.build_dir/'libaegis_microwakeword.so',
           'libc++_shared.so':stl[0],'model.tflite':args.model,'input.wav':args.wav}
    run([*adb,'shell','mkdir','-m','700',remote])
    try:
        for name,source in files.items():run([*adb,'push',source,remote+'/'+name])
        run([*adb,'shell','chmod','700',remote+'/aegis_mww_replay'])
        run([*adb,'shell','env','LD_LIBRARY_PATH='+remote,remote+'/aegis_mww_replay',remote+'/model.tflite',remote+'/input.wav',remote+'/scores.csv'])
        run([*adb,'pull',remote+'/scores.csv',args.output/'scores.csv'])
        metadata={'schema':1,'abi':abi,'deviceModel':run([*adb,'shell','getprop','ro.product.model']),
                  'androidVersion':run([*adb,'shell','getprop','ro.build.version.release']),
                  'sourceCommit':run(['git','-C',REPO,'rev-parse','HEAD']),
                  'sourceDirty':bool(run(['git','-C',REPO,'status','--porcelain'])),
                  'modelSha256':MODEL['sha256'],'wavSha256':hashlib.sha256(args.wav.read_bytes()).hexdigest(),
                  'librarySha256':hashlib.sha256(files['libaegis_microwakeword.so'].read_bytes()).hexdigest(),
                  'runnerSha256':hashlib.sha256(files['aegis_mww_replay'].read_bytes()).hexdigest(),
                  'ndkRevision':(args.ndk/'source.properties').read_text(),
                  'note':'Native replay only; does not prove real microphone/background behavior or JNI registration'}
        (args.output/'device.json').write_text(json.dumps(metadata,indent=2)+'\n')
    finally:
        for name in [*files,'scores.csv']:subprocess.run([*adb,'shell','rm','-f',remote+'/'+name],capture_output=True,check=False)
        subprocess.run([*adb,'shell','rmdir',remote],capture_output=True,check=False)
    print('Private device replay saved in '+str(args.output))


if __name__=='__main__':main()
