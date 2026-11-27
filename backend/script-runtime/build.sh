#!/bin/sh
set -eu

case "$1" in
    amd64)
        wasi_arch=x86_64
        wasmtime_arch=x86_64
        wasi_sha=c6c38aab56e5de88adf6c1ebc9c3ae8da72f88ec2b656fb024eda8d4167a0bc5
        wasmtime_sha=a18020674fac4107f329bc9ed720ea9c88e4b30a61e8a527787bcce051123d32
        api_sha=515694e57d8b376f536280a7343a69b976e83d1ce6fb57bdb87c658b18b356d3
        ;;
    arm64)
        wasi_arch=arm64
        wasmtime_arch=aarch64
        wasi_sha=ae6c1417ea161e54bc54c0a168976af57a0c6e53078857886057a71a0d928646
        wasmtime_sha=a263f34aefad1b24ba4d9ca635ddfea415f4acfe3f3f1bb6d4525c34fc6a7397
        api_sha=d18377afebb814a356119bb1b4de6b9aac7fec3151bf3d7ba112532c58b1506c
        ;;
    *) echo "Unsupported architecture for the Python WASI runtime: $1" >&2; exit 1 ;;
esac

download() {
    url=$1
    checksum=$2
    archive=${url##*/}
    curl --fail --silent --show-error --location --retry 3 "$url" --output "$archive"
    printf '%s  %s\n' "$checksum" "$archive" | sha256sum --check --status
    tar -xf "$archive"
    rm "$archive"
}

python_version=3.14.10
wasmtime_version=51.0.0
wasi_version=24
runtime=/opt/aegis-python

download "https://www.python.org/ftp/python/$python_version/Python-$python_version.tar.xz" \
    de414a57119abef52784d8993e341d53aed635fc2b05c2607d9f98a2c5b72032
download "https://github.com/WebAssembly/wasi-sdk/releases/download/wasi-sdk-$wasi_version/wasi-sdk-$wasi_version.0-$wasi_arch-linux.tar.gz" \
    "$wasi_sha"

wasmtime_dist="wasmtime-v$wasmtime_version-$wasmtime_arch-linux"
wasmtime_download="https://github.com/bytecodealliance/wasmtime/releases/download/v$wasmtime_version"
download "$wasmtime_download/$wasmtime_dist.tar.xz" "$wasmtime_sha"
download "$wasmtime_download/$wasmtime_dist-c-api.tar.xz" "$api_sha"
mv "$wasmtime_dist-c-api" /opt/wasmtime-c-api
export PATH="$PWD/$wasmtime_dist:$PATH"
export WASI_SDK_PATH="$PWD/wasi-sdk-$wasi_version.0-$wasi_arch-linux"
export SOURCE_DATE_EPOCH=1792886400
export TERM=dumb

cd "Python-$python_version"
python3 Tools/wasm/wasi configure-build-python --quiet -- --without-ensurepip --disable-test-modules
make -s -j4 -C "cross-build/$(./config.guess)"
python3 Tools/wasm/wasi configure-host --quiet -- --without-ensurepip --disable-test-modules
make -s -j4 -C cross-build/wasm32-wasip1 python.wasm

mkdir -p "$runtime/license" "$runtime/stdlib"
cp -R Lib/. "$runtime/stdlib"
rm -rf "$runtime/stdlib/test" "$runtime/stdlib/idlelib/idle_test" "$runtime/stdlib/tkinter/test"
cp LICENSE "$runtime/license/CPython"
cp /opt/wasmtime-c-api/LICENSE "$runtime/license/Wasmtime"
cp /opt/wasmtime-c-api/lib/libwasmtime.so "$runtime/"

# Match the deferred reference-counting collector used by the C API.
wasmtime compile --codegen collector=drc --wasm fuel=1000000000 --wasm max-wasm-stack=8388608 \
    --target "$(uname -m)-unknown-linux-gnu" \
    --output "$runtime/python.cwasm" cross-build/wasm32-wasip1/python.wasm
