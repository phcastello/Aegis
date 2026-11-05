#!/usr/bin/env python3
"""Export production Kotlin signal frames with deterministic synthetic speech PCM.

Uses the already cached Kotlin compiler. No Android SDK, microphone, private
recording, training model, Gradle daemon, or installed dependency is required.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess


def artifact(cache, group, name, version):
    matches=list((cache/group/name/version).glob('*/*.jar'))
    if len(matches)!=1:
        raise RuntimeError(f'Expected cached {group}:{name}:{version}; found {len(matches)}')
    return matches[0]


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--compiler-version',default='2.3.21')
    args=parser.parse_args()
    os.umask(0o077)
    args.output.mkdir(parents=True,mode=0o700,exist_ok=False)
    cache=Path.home()/'.gradle/caches/modules-2/files-2.1'
    version=args.compiler_version
    names=('kotlin-compiler-embeddable','kotlin-stdlib','kotlin-script-runtime','kotlin-reflect','kotlin-daemon-embeddable')
    jars=[artifact(cache,'org.jetbrains.kotlin',name,version) for name in names]
    jars += [artifact(cache,'org.jetbrains.kotlinx','kotlinx-coroutines-core-jvm','1.8.0'),
             artifact(cache,'org.jetbrains','annotations','23.0.0')]
    scripts=Path(__file__).resolve().parent
    source=scripts.parent/'src-tauri/gen/android/app/src/main/java/com/aegis/node/NodeHandsFreePanel.kt'
    fixture=scripts/'HandsFreeWaveformVisualFixture.kt'
    classes=args.output/'classes';classes.mkdir(mode=0o700)
    classpath=os.pathsep.join(map(str,jars))
    compile_cmd=['java','-cp',classpath,'org.jetbrains.kotlin.cli.jvm.K2JVMCompiler',
                 '-no-stdlib','-no-reflect','-classpath',classpath,'-jvm-target','1.8',
                 '-d',str(classes),str(source),str(fixture)]
    with (args.output/'compile.log').open('x') as log:
        subprocess.run(compile_cmd,stdout=log,stderr=subprocess.STDOUT,check=True)
    frames=args.output/'frames.json'
    subprocess.run(['java','-cp',os.pathsep.join([str(classes),classpath]),
                    'com.aegis.node.HandsFreeWaveformVisualFixture',str(frames)],check=True)
    data=json.loads(frames.read_text())
    digest=lambda path:hashlib.sha256(path.read_bytes()).hexdigest()
    provenance={'production_signal_sha256':digest(source),'fixture_sha256':digest(fixture),
                'frames_sha256':digest(frames),'compiler_version':version,
                'frames':len(data['frames']),'frame_rate':data['fps'],
                'scope':'Synthetic signal verification and host drawing fixture; not Android physical execution.'}
    (args.output/'provenance.json').write_text(json.dumps(provenance,indent=2)+'\n')
    print(json.dumps(provenance))


if __name__=='__main__':main()
