#!/bin/sh
# Builds JxrEncApp (jxrlib, BSD-2-Clause, Microsoft) at a pinned commit into the output directory.
# RoeSnip writes the untouched HDR original as .jxr on Linux and macOS by running it on a float TIFF
# (src/RoeSnip.Core/Imaging/JxrExport.cs); release.yml bundles it next to the app.
#   usage: sh helpers/jxrenc/build.sh <output dir> [compiler arch flags, e.g. "-arch x86_64"]
set -eu
OUT=${1:?output dir}
ARCH_FLAGS=${2:-}
HERE=$(cd "$(dirname "$0")" && pwd)
SRC=${RUNNER_TEMP:-/tmp}/jxrlib-src
COMMIT=1ef17dd8b7b6e958241914c8911d942a43837233

rm -rf "$SRC"
git clone --quiet https://github.com/4creators/jxrlib "$SRC"
git -C "$SRC" checkout --quiet "$COMMIT"

# jxrlib predates C99 strictness: current gcc/clang reject its implicit declarations and pointer
# casts unless those errors are downgraded; wchar.h is the missing include (wcslen).
FLAGS="-I. -Icommon/include -Iimage/sys -D__ANSI__ -DDISABLE_PERF_MEASUREMENT -w -O2 -include wchar.h"
FLAGS="$FLAGS -Wno-error=implicit-function-declaration -Wno-error=incompatible-pointer-types -Wno-error=int-conversion $ARCH_FLAGS"
make -C "$SRC" -j4 CC="cc $ARCH_FLAGS" CFLAGS="$FLAGS"

mkdir -p "$OUT"
cp "$SRC/build/JxrEncApp" "$OUT/JxrEncApp"
strip "$OUT/JxrEncApp"
chmod 755 "$OUT/JxrEncApp"
cp "$HERE/JXRLIB-LICENSE.txt" "$OUT/JXRLIB-LICENSE.txt"
echo "built $OUT/JxrEncApp"
