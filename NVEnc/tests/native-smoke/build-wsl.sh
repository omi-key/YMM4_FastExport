#!/usr/bin/env bash
# Cross-build NvencNative.dll and the smoke harness from Linux/WSL with mingw-w64.
# Usage: build-wsl.sh <nvEncodeAPI.h include dir> <out dir>
set -euo pipefail
HDR=${1:?nvEncodeAPI.h include dir}
OUT=${2:?output dir}
HERE=$(cd "$(dirname "$0")" && pwd)
CXX=${CXX:-x86_64-w64-mingw32-g++-posix}
"$HERE/../../NvencNative/build-mingw.sh" "$HDR" "$OUT"
"$CXX" -std=c++17 -O2 "$HERE/NvencSmoke.cpp" -o "$OUT/NvencSmoke.exe" -static -ld3d11 -ldxgi -luuid
ls -l "$OUT"
