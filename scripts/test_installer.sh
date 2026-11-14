#!/bin/bash
# test_installer.sh — end-to-end test of the release install / update / migrate path.
#
# Builds a release tarball from this checkout, then on THIS machine: installs it
# as the real systemd service, transcribes a clip through it, exercises an update
# and a failed-update rollback, uninstalls, and finally migrates a fake legacy
# source install.
#
# It installs to /opt/openscanner-ai, binds port 8090 and replaces any existing
# 'openscanner-ai' service, so it is meant for CI runners and throwaway VMs. It
# refuses to run elsewhere unless you pass --force. Ollama is not installed: the
# LLM setup is covered by test_ollama_setup.py against a mock.
set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; BLUE='\033[0;34m'; NC='\033[0m'
log_info() { echo -e "${BLUE}[TEST INFO]${NC} $1"; }
log_success() { echo -e "${GREEN}[TEST SUCCESS] $1${NC}"; }
fail() { echo -e "${RED}[TEST ERROR] $1${NC}" >&2; sudo journalctl -u openscanner-ai -n 40 --no-pager 2> /dev/null || true; exit 1; }

if [ "${CI:-}" != "true" ] && [ "${1:-}" != "--force" ]; then
    echo "This test installs the server system-wide and replaces any existing install." >&2
    echo "Run it on a CI runner or a throwaway VM, or pass --force." >&2
    exit 1
fi

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
HOME_DIR=/opt/openscanner-ai
UNIT_FILE=/etc/systemd/system/openscanner-ai.service
PORT=8090
case "$(uname -m)" in
    aarch64|arm64) RID=linux-arm64 ;;
    *) RID=linux-x64 ;;
esac

WORK=$(mktemp -d)
trap 'sudo rm -rf "$WORK"' EXIT
TARBALL="$WORK/out/openscanner-ai-$RID.tar.gz"
INSTALL=("$SCRIPT_DIR/install.sh" --tarball "$TARBALL" --yes --skip-ollama)

assert_active() { systemctl is-active --quiet openscanner-ai || fail "$1: service is not active."; }

# health <port>: wait for the server to answer with its default model loaded, then print /health.
health() {
    local body
    for _ in $(seq 1 60); do
        if body=$(curl -fsS "http://localhost:$1/health" 2> /dev/null) &&
            [ "$(echo "$body" | jq -r '.loadedModels | length')" != "0" ]; then
            echo "$body"
            return 0
        fi
        sleep 2
    done
    return 1
}

# ---------------------------------------------------------------
log_info "Step 1: Building the release tarball ($RID)..."
"$SCRIPT_DIR/build_whisper.sh" "$WORK/whisper"
"$SCRIPT_DIR/package_release.sh" "$RID" "$WORK/out" --whisper-dir "$WORK/whisper"
[ -f "$TARBALL" ] || fail "package_release.sh did not produce $TARBALL."
log_success "Built $(basename "$TARBALL")."

# ---------------------------------------------------------------
log_info "Step 2: Fresh install..."
sudo "$SCRIPT_DIR/uninstall.sh" --purge > /dev/null 2>&1 || true
"${INSTALL[@]}" --port "$PORT" --whisper-model tiny.en || fail "install.sh failed."

assert_active "After install"
HEALTH=$(health "$PORT") || fail "Server did not become ready on port $PORT."
VERSION=$(echo "$HEALTH" | jq -r '.version // empty')
[ -n "$VERSION" ] || fail "Server did not report a version."
[ "$(echo "$HEALTH" | jq -r '.status')" = "ok" ] || fail "Server is not healthy: $HEALTH"
[ "$(echo "$HEALTH" | jq -r '.defaultModel')" = "tiny.en" ] || fail "Server ignored the installed settings: $HEALTH"
log_success "Service is active and reports version $VERSION."

test -x "$HOME_DIR/app/whisper/bin/whisper-server" || fail "whisper-server is missing from the install."
test -f "$HOME_DIR/whisper/models/ggml-tiny.en.bin" || fail "The model was not stored outside the app directory."
test -f "$HOME_DIR/config/appsettings.json" || fail "Settings were not stored outside the app directory."
[ "$(stat -c %U "$HOME_DIR/config/appsettings.json")" = "$(id -un)" ] || fail "Install files are not owned by the service user."

TEXT=$(curl -fsS -X POST "http://localhost:$PORT/transcribe" -F "file=@$HOME_DIR/app/whisper/samples/jfk.wav" | jq -r '.text')
echo "$TEXT" | grep -qi "country" || fail "Transcription returned: '$TEXT'"
log_success "Transcribed a clip with the prebuilt whisper-server."

# ---------------------------------------------------------------
log_info "Step 3: Re-running the installer updates in place and keeps settings..."
jq '.Geocoding.DefaultCity = "Testville"' "$HOME_DIR/config/appsettings.json" > "$WORK/config.json"
cp "$WORK/config.json" "$HOME_DIR/config/appsettings.json"
"${INSTALL[@]}" || fail "Re-running install.sh failed."
assert_active "After update"
HEALTH=$(health "$PORT") || fail "Server did not answer on port $PORT after the update (the port was not kept)."
[ "$(echo "$HEALTH" | jq -r '.defaultModel')" = "tiny.en" ] || fail "Update did not keep the Whisper model."
[ "$(jq -r '.Geocoding.DefaultCity' "$HOME_DIR/config/appsettings.json")" = "Testville" ] || fail "Update overwrote local settings."
test -d "$HOME_DIR/app.prev" || fail "Previous version was not kept for rollback."
log_success "Update swapped in and kept settings."

# ---------------------------------------------------------------
log_info "Step 4: A broken release must roll back..."
sudo rm -rf "$HOME_DIR/staging"
mkdir -p "$HOME_DIR/staging"
tar -xzf "$TARBALL" -C "$HOME_DIR/staging"
printf '#!/bin/sh\nexit 1\n' > "$HOME_DIR/staging/openscanner-ai/server/OpenScanner.AIServer"
if sudo bash "$HOME_DIR/staging/openscanner-ai/scripts/apply_update.sh" "$HOME_DIR/staging/openscanner-ai" "$HOME_DIR" openscanner-ai; then
    fail "apply_update.sh reported success for a release that cannot start."
fi
assert_active "After rollback"
HEALTH=$(health "$PORT") || fail "Server did not answer after the rollback."
[ "$(echo "$HEALTH" | jq -r '.version')" = "$VERSION" ] || fail "Rollback did not restore the working version."
test -d "$HOME_DIR/app.failed" || fail "Failed release was not kept for inspection."
log_success "Broken release rolled back."

# ---------------------------------------------------------------
log_info "Step 5: Uninstall keeps settings and models..."
"$HOME_DIR/app/scripts/uninstall.sh" || fail "uninstall.sh failed."
systemctl is-active --quiet openscanner-ai && fail "Service is still active after uninstall."
test -d "$HOME_DIR/app" && fail "app/ survived uninstall."
test -f "$UNIT_FILE" && fail "The unit file survived uninstall."
test -f "$HOME_DIR/whisper/models/ggml-tiny.en.bin" || fail "Uninstall deleted models without --purge."
cp "$HOME_DIR/whisper/models/ggml-tiny.en.bin" "$WORK/ggml-tiny.en.bin"
"$SCRIPT_DIR/uninstall.sh" --purge || fail "uninstall.sh --purge failed."
test -e "$HOME_DIR" && fail "$HOME_DIR survived --purge."
log_success "Uninstall behaves."

# ---------------------------------------------------------------
log_info "Step 6: Migrating a legacy source install..."
# What the old installer left behind: a checkout holding the settings and models,
# and a unit that runs out of it under the pre-rename name.
LEGACY="$WORK/legacy"
LEGACY_PORT=8123
mkdir -p "$LEGACY/whisper.cpp/models"
mv "$WORK/ggml-tiny.en.bin" "$LEGACY/whisper.cpp/models/ggml-tiny.en.bin"
jq --arg dir "$LEGACY/whisper.cpp" \
    '.Whisper.DefaultModel = "tiny.en" | .Whisper.ModelsDir = ($dir + "/models")
     | .Whisper.WhisperServerBinary = ($dir + "/build/bin/whisper-server") | .Geocoding.DefaultCity = "Legacyville"' \
    "$SCRIPT_DIR/../appsettings.json" > "$LEGACY/appsettings.json"
sudo tee /etc/systemd/system/openscanner-whisper.service > /dev/null <<UNIT
[Unit]
Description=OpenScanner Whisper Server (legacy test fixture)

[Service]
Type=simple
WorkingDirectory=$LEGACY
ExecStart=/bin/sleep infinity
Environment=ASPNETCORE_URLS=http://0.0.0.0:$LEGACY_PORT

[Install]
WantedBy=multi-user.target
UNIT
sudo systemctl daemon-reload
sudo systemctl start openscanner-whisper

# No --port, --whisper-model or --migrate-from: all three come from the old install.
"${INSTALL[@]}" || fail "Migrating install failed."
assert_active "After migration"
HEALTH=$(health "$LEGACY_PORT") || fail "Migrated server did not answer on the old install's port $LEGACY_PORT."
[ "$(echo "$HEALTH" | jq -r '.defaultModel')" = "tiny.en" ] || fail "Whisper model setting was not migrated."
test -f "$HOME_DIR/whisper/models/ggml-tiny.en.bin" || fail "Whisper models were not migrated."
[ -e "$LEGACY/whisper.cpp/models/ggml-tiny.en.bin" ] && fail "Legacy model was copied instead of moved."
[ "$(jq -r '.Geocoding.DefaultCity' "$HOME_DIR/config/appsettings.json")" = "Legacyville" ] || fail "Changed settings were not migrated."
[ "$(jq -r '.Geocoding | has("Endpoint")' "$HOME_DIR/config/appsettings.json")" = "false" ] || fail "Unchanged defaults were copied into the local settings."
test -f /etc/systemd/system/openscanner-whisper.service && fail "The pre-rename unit was not removed."
systemctl is-active --quiet openscanner-whisper && fail "The pre-rename service is still running."
log_success "Legacy install migrated."

# ---------------------------------------------------------------
log_info "Step 7: Teardown..."
"$SCRIPT_DIR/uninstall.sh" --purge || fail "Final uninstall failed."

echo ""
echo "========================================="
log_success "End-to-end installer test complete!"
echo "========================================="
