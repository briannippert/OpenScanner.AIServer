#!/bin/bash
# package_release.sh — build a release tarball for one runtime.
#
#   scripts/package_release.sh <linux-x64|linux-arm64> [out_dir] [--whisper-dir <dir>]
#
# Publishes the server self-contained for the given runtime (so machines need no
# .NET runtime or SDK) and assembles <out_dir>/openscanner-ai-<rid>.tar.gz — the
# artifact that install.sh consumes:
#
#   openscanner-ai/
#     VERSION            the server version
#     server/            the published server
#     whisper/           portable CPU build of whisper-server (see build_whisper.sh)
#     scripts/           install.sh, apply_update.sh, uninstall.sh, build_whisper.sh, setup_ollama.sh
#
# --whisper-dir points at the output of build_whisper.sh for the *same*
# architecture. Without it the tarball ships no whisper-server and the installer
# builds one on the machine.
set -euo pipefail

RID=""
OUT=""
WHISPER_DIR=""
while [ $# -gt 0 ]; do
    case "$1" in
        --whisper-dir) WHISPER_DIR="${2:?--whisper-dir needs a path}"; shift 2 ;;
        -*) echo "Unknown option: $1" >&2; exit 2 ;;
        *) if [ -z "$RID" ]; then RID="$1"; else OUT="$1"; fi; shift ;;
    esac
done
case "$RID" in
    linux-x64|linux-arm64) ;;
    *) echo "Usage: $0 <linux-x64|linux-arm64> [out_dir] [--whisper-dir <dir>]" >&2; exit 2 ;;
esac

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
PROJECT="$ROOT/OpenScanner.AIServer.csproj"
OUT="${OUT:-$ROOT/dist}"
mkdir -p "$OUT"
OUT=$(cd "$OUT" && pwd)

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
APP="$STAGE/openscanner-ai"
mkdir -p "$APP"

echo "==> Publishing server ($RID, self-contained)"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true -o "$APP/server"

# The installer writes the Whisper paths for the machine it runs on; do not ship
# the ones from whichever checkout built this release.
SETTINGS=$(jq 'del(.Whisper.ModelsDir, .Whisper.WhisperServerBinary)' "$APP/server/appsettings.json")
printf '%s\n' "$SETTINGS" > "$APP/server/appsettings.json"
rm -f "$APP/server/appsettings.Development.json"

# The version nbgv stamps into the build, e.g. 0.1.42+abc1234def.
VERSION=$(dotnet msbuild "$PROJECT" -t:GetBuildVersion \
    -getProperty:AssemblyInformationalVersion 2>/dev/null | tail -n1 || true)
echo "${VERSION:-unknown}" > "$APP/VERSION"

if [ -n "$WHISPER_DIR" ]; then
    [ -x "$WHISPER_DIR/bin/whisper-server" ] || { echo "No whisper-server in $WHISPER_DIR" >&2; exit 1; }
    cp -a "$WHISPER_DIR" "$APP/whisper"
else
    echo "==> No --whisper-dir given: tarball will not include whisper-server"
fi

mkdir -p "$APP/scripts"
for s in install.sh apply_update.sh uninstall.sh build_whisper.sh setup_ollama.sh; do
    install -m 755 "$ROOT/scripts/$s" "$APP/scripts/$s"
done
if [ -f "$ROOT/LICENSE" ]; then cp "$ROOT/LICENSE" "$APP/"; fi

TARBALL="$OUT/openscanner-ai-$RID.tar.gz"
tar -C "$STAGE" --owner=0 --group=0 -czf "$TARBALL" openscanner-ai
echo "==> $TARBALL ($(du -h "$TARBALL" | cut -f1), version ${VERSION:-unknown})"
