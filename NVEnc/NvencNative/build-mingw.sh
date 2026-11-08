#!/usr/bin/env bash
# Cross-build NvencNative.dll from Linux/WSL with mingw-w64.
# Usage: build-mingw.sh <nvEncodeAPI.h include dir> <out dir>
# The header dir is e.g. nv-codec-headers' include/ffnvcodec.
set -euo pipefail
HDR=${1:?nvEncodeAPI.h include dir}
OUT=${2:?output dir}
SRC=$(cd "$(dirname "$0")" && pwd)
CXX=${CXX:-x86_64-w64-mingw32-g++-posix}   # posix threads: std::thread/mutex/condition_variable
mkdir -p "$OUT"
# mingw already defines NOMINMAX; the source redefines it -> harmless warning, silenced.
"$CXX" -std=c++17 -O2 -shared -Wno-builtin-macro-redefined \
  -I"$HDR" "$SRC/NvencNative.cpp" -o "$OUT/NvencNative.dll" \
  -static -ld3d11 -ldxgi -lmfplat -lmf -lmfuuid -lole32 -luuid 2>&1 | grep -v -E "NOMINMAX|os_defines|^\s+\||In file included|^\s+from" || true
ls -l "$OUT/NvencNative.dll"
