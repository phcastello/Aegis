#!/usr/bin/env python3
"""Run real Kotlin endpointing against host JNI/libfvad; no microphone/model/network is used.

Build aegis_microwakeword with AEGIS_BUILD_VAD_HOST_JNI=ON first. Standard Android
Gradle unit tests use a controlled classifier; this separate suite also executes
the actual native classifier and existing private WAVs. Logs/results stay private.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

REPO=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(REPO/'scripts/wakeword-poc'))
from prepare import private_output


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--library-dir',type=Path,required=True)
    parser.add_argument('--output',type=private_output,required=True)
    parser.add_argument('--private-root',type=Path,default=Path.home()/'.cache/aegis-wakeword-poc')
    parser.add_argument('--gradle-cache',type=Path,default=Path.home()/'.gradle/caches/modules-2/files-2.1')
    parser.add_argument('--kotlin-version',default='2.2.10')
    parser.add_argument('--java',default='java')
    args=parser.parse_args();os.umask(0o077)
    library=args.library_dir/'libaegis_microwakeword.so'
    if not library.is_file():parser.error('Build the host JNI library first')
    args.output.mkdir(parents=True,exist_ok=False,mode=0o700)
    def jar(group,artifact,version):
        matches=list((args.gradle_cache/group/artifact/version).glob('*/*.jar'))
        if len(matches)!=1:raise ValueError('Missing cached '+artifact+' '+version+'; prepare the locked Gradle dependencies first')
        return matches[0]
    version=args.kotlin_version
    stdlib=jar('org.jetbrains.kotlin','kotlin-stdlib',version)
    annotations=jar('org.jetbrains','annotations','13.0')
    compiler=[jar('org.jetbrains.kotlin','kotlin-compiler-embeddable',version),stdlib,
              jar('org.jetbrains.kotlin','kotlin-script-runtime',version),jar('org.jetbrains.kotlin','kotlin-daemon-embeddable',version),
              jar('org.jetbrains.kotlin','kotlin-reflect','1.6.10'),
              jar('org.jetbrains.kotlinx','kotlinx-coroutines-core-jvm','1.8.0'),annotations]
    runtime=[stdlib,jar('junit','junit','4.13.2'),jar('org.hamcrest','hamcrest-core','1.3'),annotations]
    base=REPO/'apps/aegis-node/src-tauri/gen/android/app/src'
    sources=[base/'main/java/com/aegis/node/NodeHandsFreeAudio.kt',base/'main/java/com/aegis/node/NodeHandsFreeSpeechClassifier.kt',
             base/'test/java/com/aegis/node/NodeHandsFreeAudioTest.kt',Path(__file__).with_name('NodeHandsFreeRealVadTest.kt')]
    classes=args.output/'classes';classes.mkdir()
    cp=os.pathsep.join(map(str,runtime))
    compile_cmd=[args.java,'-cp',os.pathsep.join(map(str,compiler)),'org.jetbrains.kotlin.cli.jvm.K2JVMCompiler',
                 '-no-stdlib','-no-reflect','-jvm-target','1.8','-classpath',cp,*map(str,sources),'-d',str(classes)]
    execute_cmd=[args.java,'--enable-native-access=ALL-UNNAMED','-Djava.library.path='+str(args.library_dir.resolve()),
                 '-Daegis.vad.privateRoot='+str(args.private_root.resolve()),'-cp',str(classes)+os.pathsep+cp,
                 'org.junit.runner.JUnitCore','com.aegis.node.NodeHandsFreeAudioTest','com.aegis.node.NodeHandsFreeRealVadTest']
    with (args.output/'tests.log').open('w') as log:
        subprocess.run(compile_cmd,stdout=log,stderr=subprocess.STDOUT,check=True)
        subprocess.run(execute_cmd,stdout=log,stderr=subprocess.STDOUT,check=True)
    report={'schema':1,'suite':'real Kotlin endpointing + host JNI/libfvad',
            'platform':'host JVM/native, not Android execution or physical playback',
            'nativeLibrarySha256':hashlib.sha256(library.read_bytes()).hexdigest(),
            'kotlinVersion':version,'sourcesSha256':{str(p.relative_to(REPO)):hashlib.sha256(p.read_bytes()).hexdigest() for p in sources},
            'passed':True,'modelUsed':False,'microphoneUsed':False,'audioOrTranscriptionsExported':False}
    (args.output/'result.json').write_text(json.dumps(report,indent=2)+'\n')
    print('Host VAD tests passed; private report: '+str(args.output/'result.json'))


if __name__=='__main__':main()
