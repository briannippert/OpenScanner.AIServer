#!/bin/bash
# install_service.sh — superseded by install.sh.
#
# The server used to be built from this checkout. It now installs from prebuilt
# release artifacts into /opt/openscanner-ai. This wrapper keeps the old command
# working: it runs the release installer and carries this checkout's settings
# and Whisper models over to the new location.
#
# To install a build of this checkout instead of the latest release:
#   scripts/build_whisper.sh /tmp/whisper
#   scripts/package_release.sh linux-x64 dist --whisper-dir /tmp/whisper
#   scripts/install.sh --tarball dist/openscanner-ai-linux-x64.tar.gz
set -e
SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
ARGS=()
for arg in "$@"; do
    case "$arg" in
        --skip-update) echo "[WARN] --skip-update is ignored: releases are no longer built from the checkout (see the top of this script)." ;;
        --deps-only) echo "[ERROR] --deps-only is no longer supported: releases are prebuilt, so there are no build dependencies to prepare." >&2; exit 2 ;;
        *) ARGS+=("$arg") ;;
    esac
done
echo "[INFO] install_service.sh now installs the latest release (it no longer builds from this checkout)."
exec bash "$SCRIPT_DIR/install.sh" --migrate-from "$(dirname "$SCRIPT_DIR")" "${ARGS[@]}"
