#!/usr/bin/env python3
"""Verify the APK's actual AEC3 JNI and Kotlin bridge with synthetic audio.

Build the standalone app/src/main/cpp/echo CMake project on Linux, with JAVA_HOME
pointing at the JDK. Then supply --library-dir containing libaegis_aec3.so.
"""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile

REPO = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--library-dir', type=Path, required=True)
    parser.add_argument('--gradle-cache', type=Path, default=Path.home()/'.gradle/caches/modules-2/files-2.1')
    parser.add_argument('--kotlin-version', default='2.2.10')
    parser.add_argument('--java', default='java')
    args = parser.parse_args()
    if not (args.library_dir/'libaegis_aec3.so').is_file():
        parser.error('Build the host AEC3 JNI library first')

    def jar(group, artifact, version):
        matches = list((args.gradle_cache/group/artifact/version).glob('*/*.jar'))
        if len(matches) != 1:
            raise ValueError(f'Missing cached {artifact} {version}; prepare the locked Gradle dependencies first')
        return matches[0]

    version = args.kotlin_version
    stdlib = jar('org.jetbrains.kotlin', 'kotlin-stdlib', version)
    annotations = jar('org.jetbrains', 'annotations', '13.0')
    compiler = [
        jar('org.jetbrains.kotlin', 'kotlin-compiler-embeddable', version), stdlib,
        jar('org.jetbrains.kotlin', 'kotlin-script-runtime', version),
        jar('org.jetbrains.kotlin', 'kotlin-daemon-embeddable', version),
        jar('org.jetbrains.kotlin', 'kotlin-reflect', '1.6.10'),
        jar('org.jetbrains.kotlinx', 'kotlinx-coroutines-core-jvm', '1.8.0'), annotations,
    ]
    cp = os.pathsep.join(map(str, [stdlib, annotations]))
    src = REPO/'apps/aegis-node/src-tauri/gen/android/app/src/main/java/com/aegis/node/NativeHandsFreeEcho.kt'
    test = Path(__file__).with_name('NodeHandsFreeRealEchoTest.kt')
    with tempfile.TemporaryDirectory(prefix='aegis-aec-host-') as output:
        subprocess.run([
            args.java, '-cp', os.pathsep.join(map(str, compiler)),
            'org.jetbrains.kotlin.cli.jvm.K2JVMCompiler', '-no-stdlib',
            '-no-reflect', '-jvm-target', '1.8', '-classpath', cp,
            str(src), str(test), '-d', output,
        ], check=True)
        subprocess.run([
            args.java, '--enable-native-access=ALL-UNNAMED',
            '-Djava.library.path='+str(args.library_dir.resolve()),
            '-cp', output+os.pathsep+cp, 'com.aegis.node.NodeHandsFreeRealEchoTestKt',
        ], check=True)


if __name__ == '__main__':
    main()
