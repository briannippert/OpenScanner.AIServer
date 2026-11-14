#!/bin/bash
# build_whisper.sh — build whisper-server and whisper-cli from a pinned whisper.cpp.
#
#   scripts/build_whisper.sh <out_dir> [--native | --cuda]
#
# Produces:
#   <out_dir>/bin/whisper-server         the resident transcription server
#   <out_dir>/bin/whisper-cli            used by the installer to check a model loads
#   <out_dir>/samples/jfk.wav            short clip for that check
#   <out_dir>/download-ggml-model.sh     whisper.cpp's model downloader
#   <out_dir>/LICENSE                    whisper.cpp's MIT licence
#   <out_dir>/VERSION                    the whisper.cpp ref that was built
#   <out_dir>/BACKEND                    "cpu" or "cuda"
#   <out_dir>/REQUIRES                   /proc/cpuinfo flags the binaries need (portable builds)
#
# Default is a *portable* CPU build for release artifacts: it targets a fixed CPU
# baseline rather than the build machine, and records that baseline in REQUIRES so
# the installer can tell whether a given machine can run it.
#
# --native optimises a CPU build for the current machine. --cuda does the same and
# enables NVIDIA GPU acceleration; it needs the CUDA toolkit (nvcc). CUDA builds
# are tied to the machine's CUDA version, so releases do not ship one: the
# installer runs this script on machines that have an NVIDIA GPU.
set -euo pipefail

WHISPER_CPP_REF="${WHISPER_CPP_REF:-v1.9.4}"
WHISPER_CPP_REPO="${WHISPER_CPP_REPO:-https://github.com/ggerganov/whisper.cpp.git}"

OUT=""
MODE="portable"
for arg in "$@"; do
    case "$arg" in
        --native) MODE="native" ;;
        --cuda) MODE="cuda" ;;
        -*) echo "Unknown option: $arg" >&2; exit 2 ;;
        *) OUT="$arg" ;;
    esac
done
[ -n "$OUT" ] || { echo "Usage: $0 <out_dir> [--native | --cuda]" >&2; exit 2; }
mkdir -p "$OUT"
OUT=$(cd "$OUT" && pwd)

SRC=$(mktemp -d)
trap 'rm -rf "$SRC"' EXIT

echo "==> Fetching whisper.cpp $WHISPER_CPP_REF"
git clone --quiet --depth 1 --branch "$WHISPER_CPP_REF" "$WHISPER_CPP_REPO" "$SRC/whisper.cpp"

CMAKE_ARGS=(
    -DCMAKE_BUILD_TYPE=Release
    -DBUILD_SHARED_LIBS=OFF
    -DWHISPER_BUILD_TESTS=OFF
    "-DCMAKE_EXE_LINKER_FLAGS=-static-libstdc++ -static-libgcc"
)
REQUIRES=""
BACKEND="cpu"
case "$MODE" in
    native)
        CMAKE_ARGS+=(-DGGML_NATIVE=ON)
        ;;
    cuda)
        CMAKE_ARGS+=(-DGGML_NATIVE=ON -DGGML_CUDA=ON)
        BACKEND="cuda"
        ;;
    portable)
        CMAKE_ARGS+=(-DGGML_NATIVE=OFF)
        case "$(uname -m)" in
            aarch64|arm64)
                # Cortex-A76 class and newer (Raspberry Pi 5, Jetson Orin, Graviton 2+).
                CMAKE_ARGS+=(-DGGML_CPU_ARM_ARCH=armv8.2-a+dotprod+fp16)
                REQUIRES="asimddp asimdhp"
                ;;
            x86_64)
                # ggml's non-native x86 default: AVX2-era CPUs (Haswell, 2013 and newer).
                REQUIRES="avx avx2 fma f16c"
                ;;
        esac
        ;;
esac

echo "==> Building whisper-server and whisper-cli ($MODE)"
cmake -S "$SRC/whisper.cpp" -B "$SRC/build" "${CMAKE_ARGS[@]}" > /dev/null
cmake --build "$SRC/build" --target whisper-server whisper-cli -j"$(nproc)"

# Nothing is written to <out_dir> until the build has succeeded, so a failed
# rebuild leaves a previous build there intact.
mkdir -p "$OUT/bin" "$OUT/samples"
install -m 755 "$SRC/build/bin/whisper-server" "$OUT/bin/whisper-server"
install -m 755 "$SRC/build/bin/whisper-cli" "$OUT/bin/whisper-cli"
install -m 644 "$SRC/whisper.cpp/samples/jfk.wav" "$OUT/samples/jfk.wav"
install -m 755 "$SRC/whisper.cpp/models/download-ggml-model.sh" "$OUT/download-ggml-model.sh"
install -m 644 "$SRC/whisper.cpp/LICENSE" "$OUT/LICENSE"
echo "$WHISPER_CPP_REF" > "$OUT/VERSION"
echo "$BACKEND" > "$OUT/BACKEND"
echo "$REQUIRES" > "$OUT/REQUIRES"

echo "==> whisper-server ($BACKEND) written to $OUT/bin/whisper-server"
