#!/bin/bash
# ==========================================
# OpenScanner AI Server installer (Linux)
# ==========================================
# Installs the server from a prebuilt release — no repo clone or .NET SDK needed:
#
#   curl -fsSL https://github.com/briannippert/OpenScanner.AIServer/releases/latest/download/install.sh | bash
#
# Run it as your normal user; it asks for sudo when it needs it. Re-running it
# repairs the install and moves it to the latest release, keeping your settings
# and models. An older install that was built from a git clone is detected and
# migrated automatically.
#
# Options (--name=value and --name value both work):
#   --version <tag>         install a specific release (default: latest)
#   --tarball <path>        install from a local release tarball instead of downloading
#   --migrate-from <dir>    carry settings and models over from an old git-clone install
#   --port <n>              port to listen on (otherwise prompted; default 8090)
#   --whisper-model <name>  default Whisper model (otherwise prompted)
#   --llm-model <name>      Ollama model (otherwise prompted; --ollama-model is an alias)
#   --ollama-url <url>      Ollama origin (otherwise the configured Address:OllamaUrl)
#   --upgrade-ollama        force an upgrade of the managed local Ollama
#   --skip-ollama           leave Ollama and the Address settings untouched
#   --cpu-only              do not build or use GPU acceleration
#   --rebuild-whisper       rebuild whisper-server on this machine even if one exists
#   --no-model              do not download or check the Whisper model
#   --user <name>           account the service runs as (default: the installing user)
#   --yes                   never prompt; keep current settings or use the defaults
#
# Layout under /opt/openscanner-ai (override with OPENSCANNER_AI_HOME):
#   app/        the release; replaced wholesale by every update
#   app.prev/   the previous release, kept for rollback
#   config/     appsettings.json overrides             (kept across updates)
#   whisper/    models, and a whisper-server built on this machine if one was
#               needed (always the case with an NVIDIA GPU)
set -euo pipefail

REPO_SLUG="briannippert/OpenScanner.AIServer"
HOME_DIR="${OPENSCANNER_AI_HOME:-/opt/openscanner-ai}"
UNIT="openscanner-ai"
# Installs before the rename to OpenScanner.AIServer used this unit name.
LEGACY_UNIT="openscanner-whisper"
SYSTEMD_DIR="${OPENSCANNER_AI_SYSTEMD_DIR:-/etc/systemd/system}"
SERVICE_FILE="$SYSTEMD_DIR/$UNIT.service"
LEGACY_SERVICE_FILE="$SYSTEMD_DIR/$LEGACY_UNIT.service"
INSTALL_URL="https://github.com/$REPO_SLUG/releases/latest/download/install.sh"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; BLUE='\033[0;34m'; NC='\033[0m'; BOLD='\033[1m'
log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_step() { echo -e "\n${BLUE}${BOLD}==> $1${NC}"; }
log_success() { echo -e "${GREEN}[OK] $1${NC}"; }
log_warn() { echo -e "${YELLOW}[WARN] $1${NC}"; }
log_error() { echo -e "${RED}[ERROR] $1${NC}" >&2; }
die() { log_error "$1"; exit 1; }

# ----------------------------------------------------------------
# Arguments
# ----------------------------------------------------------------
VERSION="latest"
TARBALL=""
LEGACY_ROOT=""
PORT=""
WHISPER_MODEL_CHOICE=""
LLM_MODEL_CHOICE=""
OLLAMA_URL_CHOICE=""
UPGRADE_OLLAMA=false
SKIP_OLLAMA=false
CPU_ONLY=false
REBUILD_WHISPER=false
FETCH_MODEL=true
SERVICE_USER=""
ASSUME_DEFAULTS=false
while [ $# -gt 0 ]; do
    arg="$1"; shift
    case "$arg" in
        --*=*) opt="${arg%%=*}"; val="${arg#*=}"; has_val=true ;;
        *) opt="$arg"; val=""; has_val=false ;;
    esac
    case "$opt" in
        --version|--tarball|--migrate-from|--port|--whisper-model|--llm-model|--ollama-model|--ollama-url|--user)
            if [ "$has_val" = false ]; then
                [ $# -gt 0 ] || { log_error "$opt needs a value (use --help)"; exit 2; }
                val="$1"; shift
            fi ;;
        *) [ "$has_val" = false ] || { log_error "$opt does not take a value (use --help)"; exit 2; } ;;
    esac
    case "$opt" in
        --version) VERSION="$val" ;;
        --tarball) TARBALL="$val" ;;
        --migrate-from) LEGACY_ROOT="$val" ;;
        --port) PORT="$val" ;;
        --whisper-model) WHISPER_MODEL_CHOICE="$val" ;;
        --llm-model|--ollama-model) LLM_MODEL_CHOICE="$val" ;;
        --ollama-url) OLLAMA_URL_CHOICE="$val" ;;
        --user) SERVICE_USER="$val" ;;
        --upgrade-ollama) UPGRADE_OLLAMA=true ;;
        --skip-ollama) SKIP_OLLAMA=true ;;
        --cpu-only) CPU_ONLY=true ;;
        --rebuild-whisper) REBUILD_WHISPER=true ;;
        --no-model) FETCH_MODEL=false ;;
        --yes|-y) ASSUME_DEFAULTS=true ;;
        -h|--help)
            if [ -f "${BASH_SOURCE[0]:-}" ]; then
                sed -n '2,35p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
            else
                echo "Options are listed at https://github.com/$REPO_SLUG#installer-reference"
            fi
            exit 0 ;;
        *) log_error "Unknown option: $opt (use --help)"; exit 2 ;;
    esac
done

valid_port() { [[ "$1" =~ ^[0-9]{1,5}$ ]] && (( 10#$1 >= 1 && 10#$1 <= 65535 )); }
valid_whisper_model() { [[ "$1" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$ ]]; }

if [ "$SKIP_OLLAMA" = true ] && { [ "$UPGRADE_OLLAMA" = true ] ||
    [ -n "$LLM_MODEL_CHOICE" ] || [ -n "$OLLAMA_URL_CHOICE" ]; }; then
    die "--skip-ollama cannot be combined with Ollama model, URL or upgrade options."
fi
if [ -n "$PORT" ] && ! valid_port "$PORT"; then die "--port must be a number from 1 to 65535."; fi
if [ -n "$WHISPER_MODEL_CHOICE" ] && ! valid_whisper_model "$WHISPER_MODEL_CHOICE"; then
    die "Invalid Whisper model name. Use a name such as large-v3-turbo-q5_0."
fi

case "$(uname -m)" in
    aarch64|arm64) ARCH="arm64" ;;
    x86_64|amd64) ARCH="x64" ;;
    *) die "Unsupported CPU architecture '$(uname -m)'. Releases are built for x86-64 and 64-bit ARM." ;;
esac
ASSET="openscanner-ai-linux-$ARCH.tar.gz"

command -v apt-get > /dev/null || die "This installer supports Debian-based systems (Ubuntu, Debian)."
command -v systemctl > /dev/null || die "systemd is required to run the server as a service."

# Everything below runs as the invoking user and uses sudo for the few steps
# that need root, so the files it creates belong to the account the service
# runs as. Started as root there is nothing to elevate.
if [ "$EUID" -eq 0 ]; then
    sudo() { "$@"; }
    RUN_USER="${SERVICE_USER:-${SUDO_USER:-root}}"
else
    command -v sudo > /dev/null || die "This installer needs 'sudo' to install packages and the systemd service."
    RUN_USER="${SERVICE_USER:-$(id -un)}"
fi
id "$RUN_USER" > /dev/null 2>&1 || die "User '$RUN_USER' does not exist."

# Piped from curl, stdin is this script, so questions are asked on the terminal.
PROMPT_IN=""
if [ "$ASSUME_DEFAULTS" = false ]; then
    if [ -t 0 ]; then PROMPT_IN="/dev/stdin"
    elif { : < /dev/tty; } 2> /dev/null; then PROMPT_IN="/dev/tty"
    fi
fi
# ask <prompt>: sets REPLY, empty when there is nobody to ask.
ask() {
    REPLY=""
    [ -n "$PROMPT_IN" ] || return 0
    read -r -p "$(echo -e "${BLUE}[INFO]${NC} $1")" REPLY < "$PROMPT_IN" || REPLY=""
}

[ -z "$TARBALL" ] || TARBALL=$(readlink -f "$TARBALL")
[ -z "$LEGACY_ROOT" ] || LEGACY_ROOT=$(readlink -f "$LEGACY_ROOT")

log_step "Initializing"
log_info "Install root: $HOME_DIR"
log_info "Architecture: linux-$ARCH"
log_info "Service user: $RUN_USER"

# An install made by the old source installer has a unit that runs out of the
# checkout. Find that checkout so its settings and models can be carried over.
EXISTING_UNIT_FILE=""
if [ -f "$SERVICE_FILE" ]; then EXISTING_UNIT_FILE="$SERVICE_FILE"
elif [ -f "$LEGACY_SERVICE_FILE" ]; then EXISTING_UNIT_FILE="$LEGACY_SERVICE_FILE"
fi
if [ -z "$LEGACY_ROOT" ] && [ -n "$EXISTING_UNIT_FILE" ] && ! grep -q 'OPENSCANNER_AI_HOME=' "$EXISTING_UNIT_FILE"; then
    LEGACY_ROOT=$(sed -n 's/^WorkingDirectory=//p' "$EXISTING_UNIT_FILE" | head -n1)
fi
if [ -n "$LEGACY_ROOT" ]; then
    if [ -f "$LEGACY_ROOT/appsettings.json" ] && [ "$LEGACY_ROOT" != "$HOME_DIR" ]; then
        log_info "Existing source install found at $LEGACY_ROOT — it will be migrated."
    else
        LEGACY_ROOT=""
    fi
fi

if ! mkdir -p "$HOME_DIR" 2> /dev/null || [ ! -w "$HOME_DIR" ]; then
    sudo mkdir -p "$HOME_DIR"
    sudo chown "$RUN_USER" "$HOME_DIR"
fi

# ----------------------------------------------------------------
# 1. System dependencies
# ----------------------------------------------------------------
log_step "Installing system libraries..."
sudo apt-get update -qq || log_warn "apt-get update reported errors; continuing."
# libicu's package name carries its version (libicu72, libicu74, ...); the
# self-contained .NET runtime needs whichever one this distro ships.
ICU_PKG=$(apt-cache pkgnames libicu 2> /dev/null | grep -E '^libicu[0-9]+$' | sort -V | tail -n1 || true)
# shellcheck disable=SC2086
sudo apt-get install -y -qq curl ca-certificates tar jq ffmpeg zstd libgomp1 $ICU_PKG > /dev/null
log_success "Libraries installed."

# ----------------------------------------------------------------
# 2. Fetch and unpack the release
# ----------------------------------------------------------------
STAGING="$HOME_DIR/staging"
NEW="$STAGING/openscanner-ai"
rm -rf "$STAGING"
mkdir -p "$STAGING"

if [ -n "$TARBALL" ]; then
    log_step "Using local release $TARBALL"
    [ -f "$TARBALL" ] || die "Tarball not found: $TARBALL"
else
    if [ "$VERSION" = "latest" ]; then
        BASE_URL="https://github.com/$REPO_SLUG/releases/latest/download"
    else
        BASE_URL="https://github.com/$REPO_SLUG/releases/download/$VERSION"
    fi
    log_step "Downloading release ($VERSION)..."
    TARBALL="$STAGING/$ASSET"
    curl -fL --retry 3 --progress-bar -o "$TARBALL" "$BASE_URL/$ASSET" \
        || die "Could not download $BASE_URL/$ASSET"
    curl -fsSL --retry 3 -o "$STAGING/SHA256SUMS" "$BASE_URL/SHA256SUMS" \
        || die "Could not download $BASE_URL/SHA256SUMS"

    EXPECTED=$(awk -v f="$ASSET" '{ n = $2; sub(/^\*/, "", n); if (n == f) print $1 }' "$STAGING/SHA256SUMS" | head -n1)
    ACTUAL=$(sha256sum "$TARBALL" | awk '{print $1}')
    if [ -z "$EXPECTED" ] || [ "$EXPECTED" != "$ACTUAL" ]; then
        die "Checksum mismatch for $ASSET (expected '${EXPECTED:-none listed}', got $ACTUAL)."
    fi
    log_success "Checksum verified."
fi

tar -xzf "$TARBALL" -C "$STAGING"
[ -x "$NEW/server/OpenScanner.AIServer" ] || die "The release archive is missing server/OpenScanner.AIServer."
RELEASE_VERSION=$(cat "$NEW/VERSION" 2> /dev/null || echo "unknown")
log_info "Release version: $RELEASE_VERSION"

# ----------------------------------------------------------------
# 3. Settings
# ----------------------------------------------------------------
# Machine-local settings live in config/appsettings.json, layered over the
# release's own appsettings.json. Changes are made to a working copy and only
# put in place once the new release is about to start.
CONFIG="$HOME_DIR/config/appsettings.json"
CONFIG_NEW="$STAGING/appsettings.local.json"
RELEASE_SETTINGS="$NEW/server/appsettings.json"
if [ -f "$CONFIG" ]; then
    jq -e 'type == "object"' "$CONFIG" > /dev/null 2>&1 || die "$CONFIG is not valid JSON. Fix or remove it, then re-run."
    cp "$CONFIG" "$CONFIG_NEW"
elif [ -n "$LEGACY_ROOT" ]; then
    # Carry over only what differs from the release's defaults, so settings that
    # were never changed keep following future releases. The Whisper paths are
    # rewritten below for the new layout.
    jq -n --slurpfile old "$LEGACY_ROOT/appsettings.json" --slurpfile new "$RELEASE_SETTINGS" '
        def changed($a; $b):
            if ($a | type) == "object" and ($b | type) == "object" then
                reduce ($a | keys_unsorted[]) as $k ({};
                    changed($a[$k]; $b[$k]) as $d | if $d == null then . else .[$k] = $d end)
                | if length == 0 then null else . end
            elif $a == $b then null
            else $a end;
        (changed($old[0]; $new[0]) // {})
        | del(.Logging, .AllowedHosts, .Urls, .Whisper.ModelsDir, .Whisper.WhisperServerBinary)
        | if .Whisper == {} then del(.Whisper) else . end' > "$CONFIG_NEW" \
        || die "Could not read settings from $LEGACY_ROOT/appsettings.json."
    log_info "Carried over settings from $LEGACY_ROOT/appsettings.json."
else
    echo '{}' > "$CONFIG_NEW"
fi

# setting <jq path> <fallback>: the effective value — local override, else the release default.
setting() {
    local value
    value=$(jq -r "$1 // empty" "$CONFIG_NEW" 2> /dev/null || true)
    [ -n "$value" ] || value=$(jq -r "$1 // empty" "$RELEASE_SETTINGS" 2> /dev/null || true)
    echo "${value:-$2}"
}

log_step "Whisper model"
CURRENT_MODEL=$(setting '.Whisper.DefaultModel' 'large-v3-turbo-q5_0')
if [ -n "$WHISPER_MODEL_CHOICE" ]; then
    WHISPER_MODEL="$WHISPER_MODEL_CHOICE"
elif [ -z "$PROMPT_IN" ]; then
    WHISPER_MODEL="$CURRENT_MODEL"
else
    echo ""
    echo -e "  Default Whisper model to keep resident (models can be switched per-request"
    echo -e "  from OpenScanner; this one is pre-warmed at startup):"
    echo ""
    echo -e "    ${BOLD}1)${NC} tiny.en              - Fastest, lowest accuracy (~75 MB)"
    echo -e "    ${BOLD}2)${NC} base.en              - Fast, decent accuracy (~150 MB)"
    echo -e "    ${BOLD}3)${NC} small.en             - Good balance (~500 MB)"
    echo -e "    ${BOLD}4)${NC} medium.en            - High accuracy, slower (~1.5 GB)"
    echo -e "    ${BOLD}5)${NC} large-v3-turbo-q5_0  - Best accuracy, fast on GPU (~550 MB) [recommended]"
    echo -e "    ${BOLD}6)${NC} large-v3             - Best accuracy, slowest (~3 GB)"
    echo ""
    ask "Select a model [1-6] (press Enter to keep ${BOLD}$CURRENT_MODEL${NC}): "
    case "$REPLY" in
        1) WHISPER_MODEL="tiny.en" ;;
        2) WHISPER_MODEL="base.en" ;;
        3) WHISPER_MODEL="small.en" ;;
        4) WHISPER_MODEL="medium.en" ;;
        5) WHISPER_MODEL="large-v3-turbo-q5_0" ;;
        6) WHISPER_MODEL="large-v3" ;;
        *) WHISPER_MODEL="$CURRENT_MODEL" ;;
    esac
fi
valid_whisper_model "$WHISPER_MODEL" || die "Configured Whisper model name '$WHISPER_MODEL' is invalid."
log_info "Using model: $WHISPER_MODEL"

if [ -z "$PORT" ]; then
    # The port lives in the systemd unit, for both this layout and the old one.
    CURRENT_PORT=""
    if [ -n "$EXISTING_UNIT_FILE" ]; then
        CURRENT_PORT=$(sed -n 's/^Environment=ASPNETCORE_URLS=.*:\([0-9][0-9]*\).*$/\1/p' "$EXISTING_UNIT_FILE" | head -n1)
    fi
    valid_port "${CURRENT_PORT:-x}" || CURRENT_PORT="8090"
    ask "Port to run the server on (press Enter to keep ${BOLD}$CURRENT_PORT${NC}): "
    PORT="${REPLY:-$CURRENT_PORT}"
    valid_port "$PORT" || die "Port must be a number from 1 to 65535."
fi
log_info "Using port: $PORT"

# ----------------------------------------------------------------
# 4. Ollama and the metadata/address model
# ----------------------------------------------------------------
if [ "$SKIP_OLLAMA" = true ]; then
    log_info "Skipping Ollama setup (--skip-ollama); Address configuration is unchanged."
else
    log_step "Setting up Ollama and the metadata LLM..."
    # shellcheck source=setup_ollama.sh
    source "$NEW/scripts/setup_ollama.sh"
    if [ -z "$PROMPT_IN" ] && [ -z "$LLM_MODEL_CHOICE" ]; then
        # Nobody to ask: keep the configured model.
        LLM_MODEL_CHOICE="${Address__Model:-$(setting '.Address.Model' 'qwen3:4b')}"
    fi
    if [ -z "$OLLAMA_URL_CHOICE" ] && [ -z "${Address__OllamaUrl:-}" ]; then
        OLLAMA_URL_CHOICE=$(setting '.Address.OllamaUrl' 'http://127.0.0.1:11434/')
    fi
    setup_ollama "$CONFIG_NEW" "$LLM_MODEL_CHOICE" "$OLLAMA_URL_CHOICE" "$UPGRADE_OLLAMA" < "${PROMPT_IN:-/dev/null}"
fi

# ----------------------------------------------------------------
# 5. whisper-server
# ----------------------------------------------------------------
log_step "Checking whisper-server..."
mkdir -p "$HOME_DIR/whisper/models"
DEVICE_WHISPER="$HOME_DIR/whisper"
BUNDLED_VERSION=$(cat "$NEW/whisper/VERSION" 2> /dev/null || true)

has_nvidia() { command -v nvidia-smi > /dev/null 2>&1 && nvidia-smi > /dev/null 2>&1; }

# Can this CPU run the prebuilt whisper-server? REQUIRES lists the /proc/cpuinfo
# flags it was compiled for; a missing one means an illegal-instruction crash
# mid-transcription rather than at startup, so check up front.
bundled_whisper_ok() {
    local bin="$NEW/whisper/bin/whisper-server" flag
    [ -x "$bin" ] || return 1
    for flag in $(cat "$NEW/whisper/REQUIRES" 2> /dev/null); do
        grep -m1 -E '^(flags|Features)' /proc/cpuinfo | grep -w "$flag" > /dev/null || return 1
    done
    "$bin" --help > /dev/null 2>&1
}
device_whisper_ok() {
    [ -x "$DEVICE_WHISPER/bin/whisper-server" ] && "$DEVICE_WHISPER/bin/whisper-server" --help > /dev/null 2>&1
}
device_backend() { cat "$DEVICE_WHISPER/BACKEND" 2> /dev/null || echo "cpu"; }
build_device_whisper() {
    sudo apt-get install -y -qq git cmake build-essential > /dev/null
    bash "$NEW/scripts/build_whisper.sh" "$DEVICE_WHISPER" "$1"
}
remove_device_whisper() {
    rm -rf "$DEVICE_WHISPER/bin" "$DEVICE_WHISPER/samples"
    rm -f "$DEVICE_WHISPER/download-ggml-model.sh" "$DEVICE_WHISPER/LICENSE" \
        "$DEVICE_WHISPER/VERSION" "$DEVICE_WHISPER/BACKEND" "$DEVICE_WHISPER/REQUIRES"
}

# WHISPER_NOW is where the chosen build is right now; WHISPER_FINAL is where it
# will be once the release has been swapped into app/.
WHISPER_NOW=""
WHISPER_FINAL=""
USE_GPU=false
use_device_whisper() {
    WHISPER_NOW="$DEVICE_WHISPER"; WHISPER_FINAL="$DEVICE_WHISPER"
    if [ "$(device_backend)" = "cuda" ]; then USE_GPU=true; fi
}

if [ "$CPU_ONLY" = false ] && has_nvidia; then
    GPU_NAME=$(nvidia-smi --query-gpu=name --format=csv,noheader 2> /dev/null | head -n1 || true)
    log_success "NVIDIA GPU detected: ${GPU_NAME:-unknown}"
    # A CUDA build is tied to this machine's CUDA version, so it is built here.
    if [ "$REBUILD_WHISPER" = false ] && device_whisper_ok && [ "$(device_backend)" = "cuda" ] &&
        [ "$(cat "$DEVICE_WHISPER/VERSION" 2> /dev/null)" = "$BUNDLED_VERSION" ]; then
        log_success "Using the GPU whisper-server already built on this machine."
        use_device_whisper
    else
        if ! command -v nvcc > /dev/null 2>&1; then
            log_info "Installing the NVIDIA CUDA toolkit..."
            sudo apt-get install -y -qq nvidia-cuda-toolkit > /dev/null || log_warn "Could not install nvidia-cuda-toolkit."
        fi
        log_info "Building whisper-server with CUDA for this machine (several minutes)..."
        if command -v nvcc > /dev/null 2>&1 && build_device_whisper --cuda; then
            log_success "whisper-server built with GPU acceleration."
            use_device_whisper
        elif device_whisper_ok && [ "$(device_backend)" = "cuda" ]; then
            log_warn "The CUDA build failed; keeping the GPU whisper-server built earlier."
            use_device_whisper
        else
            log_warn "The CUDA build failed; falling back to CPU transcription. Re-run with --rebuild-whisper once CUDA is fixed."
        fi
    fi
else
    log_info "Using CPU transcription (no NVIDIA GPU detected, or --cpu-only)."
fi

if [ -z "$WHISPER_NOW" ]; then
    if [ "$REBUILD_WHISPER" = false ] && bundled_whisper_ok; then
        log_success "Using the prebuilt whisper-server."
        # A copy built here by an earlier install is no longer the one in use.
        remove_device_whisper
        WHISPER_NOW="$NEW/whisper"; WHISPER_FINAL="$HOME_DIR/app/whisper"
    elif [ "$REBUILD_WHISPER" = false ] && device_whisper_ok && [ "$(device_backend)" = "cpu" ]; then
        log_success "Using the whisper-server previously built on this machine."
        use_device_whisper
    else
        log_warn "No usable prebuilt whisper-server for this CPU; building one here (several minutes)..."
        if build_device_whisper --native; then
            log_success "whisper-server built for this machine."
            use_device_whisper
        else
            log_warn "whisper-server build failed — transcription will be unavailable until it is fixed."
            WHISPER_NOW=""; WHISPER_FINAL="$DEVICE_WHISPER"
        fi
    fi
fi

# ----------------------------------------------------------------
# 6. Migrate an old source install
# ----------------------------------------------------------------
# From here on a running install is being changed, so any failure has to put it
# back: MOVED records "new<TAB>old" for every model that was moved, and a
# service that was stopped is started again.
MOVED=()
UNIT_BACKUP=""
UNIT_CREATED=false
STOPPED_UNIT=""
SWITCHED=false

restore_previous_install() {
    local status=$? entry
    trap - EXIT
    if [ "$status" -ne 0 ] && [ "$SWITCHED" = false ]; then
        if [ ${#MOVED[@]} -gt 0 ]; then
            log_error "Install failed — restoring the previous install at $LEGACY_ROOT"
            for entry in "${MOVED[@]}"; do
                mv "${entry%%$'\t'*}" "${entry##*$'\t'}" 2> /dev/null || log_error "Could not restore ${entry##*$'\t'}"
            done
        fi
        # Put the unit back the way it was: the old definition, or no unit at all.
        if [ -n "$UNIT_BACKUP" ] && [ -f "$UNIT_BACKUP" ]; then
            sudo install -m 644 "$UNIT_BACKUP" "$SERVICE_FILE" || true
            sudo systemctl daemon-reload || true
        elif [ "$UNIT_CREATED" = true ]; then
            sudo systemctl disable --quiet "$UNIT" 2> /dev/null || true
            sudo rm -f "$SERVICE_FILE"
            sudo systemctl daemon-reload || true
        fi
        if [ -n "$STOPPED_UNIT" ]; then
            # restart, not start: a rolled-back release may already be running
            # under the unit definition that was just replaced.
            log_info "Restoring the previously running service ($STOPPED_UNIT)..."
            sudo systemctl restart "$STOPPED_UNIT" || log_warn "Could not restore the service; check systemctl status $STOPPED_UNIT."
        fi
    fi
    [ -z "$UNIT_BACKUP" ] || rm -f "$UNIT_BACKUP"
    exit "$status"
}
trap restore_previous_install EXIT

# Only one unit is ever restored, so the old and new services never compete for the port.
stop_service() {
    local unit
    for unit in "$UNIT" "$LEGACY_UNIT"; do
        if systemctl is-active --quiet "$unit"; then
            sudo systemctl stop "$unit"
            if [ -z "$STOPPED_UNIT" ]; then STOPPED_UNIT="$unit"; fi
            log_info "Stopped $unit."
        fi
    done
}

if [ -n "$LEGACY_ROOT" ]; then
    log_step "Migrating models from $LEGACY_ROOT..."
    # The old install loads models from its checkout, so it is stopped before they move.
    stop_service
    for model in "$LEGACY_ROOT"/whisper.cpp/models/ggml-*.bin; do
        [ -f "$model" ] || continue
        case "$(basename "$model")" in for-tests-*) continue ;; esac
        target="$HOME_DIR/whisper/models/$(basename "$model")"
        if [ -e "$target" ]; then
            log_warn "Keeping existing $target; left $model in place."
            continue
        fi
        mv "$model" "$target"
        MOVED+=("$target"$'\t'"$model")
        log_info "Moved $(basename "$model")"
    done
    log_success "Migration staged."
fi

# ----------------------------------------------------------------
# 7. Whisper model
# ----------------------------------------------------------------
MODELS_DIR="$HOME_DIR/whisper/models"
MODEL_BIN="$MODELS_DIR/ggml-$WHISPER_MODEL.bin"

download_model() {
    log_info "Downloading Whisper model ($WHISPER_MODEL)..."
    bash "$WHISPER_NOW/download-ggml-model.sh" "$WHISPER_MODEL" "$MODELS_DIR"
}
verify_model() {
    # Loading the model reads every tensor, so a truncated file fails here. Run on
    # CPU (-ng) with a 1s decode so this is a fast integrity check, not a benchmark,
    # and independent of available VRAM.
    "$WHISPER_NOW/bin/whisper-cli" -m "$MODEL_BIN" -f "$WHISPER_NOW/samples/jfk.wav" \
        -ng -nt -d 1000 > /dev/null 2>&1
}

if [ "$FETCH_MODEL" = false ]; then
    log_info "Skipping the Whisper model (--no-model)."
elif [ -z "$WHISPER_NOW" ]; then
    log_warn "No whisper-server available; skipping the Whisper model."
else
    log_step "Checking Whisper model ($WHISPER_MODEL)..."
    # An incomplete .bin left by an interrupted download would otherwise be
    # skipped forever because the file "exists", and whisper-server would fail to
    # load it. So every model is load-tested, and re-downloaded once if that fails.
    [ -f "$MODEL_BIN" ] || download_model || die "Could not download the Whisper model. Check network access and free disk space."
    if [ -x "$WHISPER_NOW/bin/whisper-cli" ] && [ -f "$WHISPER_NOW/samples/jfk.wav" ]; then
        log_info "Verifying model integrity..."
        if ! verify_model; then
            log_warn "Model failed to load (incomplete/corrupt download). Re-downloading..."
            rm -f "$MODEL_BIN"
            download_model || die "Could not download the Whisper model. Check network access and free disk space."
            verify_model || die "Model still fails to load after re-download. Check free disk space and network."
        fi
        log_success "Whisper model ready and verified."
    else
        log_success "Whisper model downloaded (skipped load check: whisper-cli/sample unavailable)."
    fi
fi

# ----------------------------------------------------------------
# 8. Size the resident-model pool to the GPU and selected model
# ----------------------------------------------------------------
log_step "Sizing model pool..."

# Each resident whisper-server process holds a full copy of the model plus compute
# buffers. Estimate per-instance VRAM from the model file size (weights ~1.35x on
# GPU + a fixed buffer allowance), then fit as many process "slots" as the GPU has.
MODEL_MB=0
if [ -f "$MODEL_BIN" ]; then MODEL_MB=$(du -m "$MODEL_BIN" | cut -f1); fi
PER_INSTANCE_MB=$(( MODEL_MB * 135 / 100 + 700 ))

VRAM_MB=0
if [ "$USE_GPU" = true ]; then
    VRAM_MB=$(nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2> /dev/null | head -n1 | tr -dc '0-9' || true)
    [ -n "$VRAM_MB" ] || VRAM_MB=0
fi

# Reserve memory for the co-located address model. Set to 0 when Ollama runs
# elsewhere or address extraction is unused. This is a budget, not enforcement.
ADDRESS_RESERVE_MB=$(setting '.Address.ReservedVramMb' '3500')
if ! [[ "$ADDRESS_RESERVE_MB" =~ ^[0-9]+$ ]] || [ "${#ADDRESS_RESERVE_MB}" -gt 6 ]; then
    die "Address.ReservedVramMb must be an integer from 0 to 999999."
fi
ADDRESS_RESERVE_MB=$((10#$ADDRESS_RESERVE_MB))

if [ "$USE_GPU" = true ] && [ "$VRAM_MB" -gt 0 ]; then
    # Leave headroom for the display/driver: 15% of VRAM, capped at 1500 MB.
    RESERVE=$(( VRAM_MB * 15 / 100 )); [ "$RESERVE" -gt 1500 ] && RESERVE=1500
    USABLE=$(( VRAM_MB - RESERVE - ADDRESS_RESERVE_MB ))
    [ "$USABLE" -lt 0 ] && USABLE=0
    log_info "Reserving ${ADDRESS_RESERVE_MB} MB for address extraction."
    SLOTS=$(( USABLE / PER_INSTANCE_MB ))
    [ "$SLOTS" -lt 1 ] && SLOTS=1

    if [ "$SLOTS" -ge 2 ]; then INSTANCES=2; else INSTANCES=1; fi
    MAXMODELS=$(( SLOTS / INSTANCES ))
    [ "$MAXMODELS" -lt 1 ] && MAXMODELS=1
    [ "$MAXMODELS" -gt 2 ] && MAXMODELS=2

    log_info "GPU VRAM: ${VRAM_MB} MB (usable ~${USABLE} MB), est. ${PER_INSTANCE_MB} MB/instance for $WHISPER_MODEL"
    if [ "$USABLE" -lt "$PER_INSTANCE_MB" ]; then
        log_warn "Model may not fit in VRAM; it will still run but could fall back/slow down."
    fi
else
    # CPU build (or no VRAM reading): one warm instance of one model. CPU inference
    # already uses all cores, so extra instances mostly contend.
    INSTANCES=1
    MAXMODELS=1
    log_info "No CUDA GPU sizing available; using a single resident instance."
fi

WHISPER_BIN="$WHISPER_FINAL/bin/whisper-server"
UPDATED=$(jq \
    --arg bin "$WHISPER_BIN" \
    --arg modelsDir "$MODELS_DIR" \
    --arg defaultModel "$WHISPER_MODEL" \
    --argjson instances "$INSTANCES" \
    --argjson maxModels "$MAXMODELS" \
    --argjson useGpu "$USE_GPU" \
    '.Whisper.WhisperServerBinary = $bin | .Whisper.ModelsDir = $modelsDir | .Whisper.DefaultModel = $defaultModel | .Whisper.InstancesPerModel = $instances | .Whisper.MaxResidentModels = $maxModels | .Whisper.UseGpu = $useGpu' \
    "$CONFIG_NEW")
printf '%s\n' "$UPDATED" > "$CONFIG_NEW"

log_info "  Binary:       $WHISPER_BIN"
log_info "  Models dir:   $MODELS_DIR"
log_info "  Default:      $WHISPER_MODEL"
log_info "  Instances:    $INSTANCES per model  (max $MAXMODELS resident model(s), GPU=$USE_GPU)"

# ----------------------------------------------------------------
# 9. Switch over: settings, systemd unit, release
# ----------------------------------------------------------------
log_step "Configuring systemd service..."
stop_service

mkdir -p "$HOME_DIR/config"
CONFIG_BACKUP=""
if [ -f "$CONFIG" ]; then
    CONFIG_BACKUP="$HOME_DIR/config/appsettings.json.prev"
    cp "$CONFIG" "$CONFIG_BACKUP"
fi
cp "$CONFIG_NEW" "$CONFIG"

if [ -f "$SERVICE_FILE" ]; then
    UNIT_BACKUP=$(mktemp)
    cat "$SERVICE_FILE" > "$UNIT_BACKUP"
else
    UNIT_CREATED=true
fi
UNIT_TMP=$(mktemp)
cat > "$UNIT_TMP" <<EOF
[Unit]
Description=OpenScanner AI Server
After=network.target ollama.service

[Service]
Type=simple
User=$RUN_USER
WorkingDirectory=$HOME_DIR/app/server
ExecStart=$HOME_DIR/app/server/OpenScanner.AIServer
Restart=always
RestartSec=5
Environment=DOTNET_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:$PORT
Environment=OPENSCANNER_AI_HOME=$HOME_DIR

[Install]
WantedBy=multi-user.target
EOF
sudo install -m 644 "$UNIT_TMP" "$SERVICE_FILE"
rm -f "$UNIT_TMP"
sudo systemctl daemon-reload
sudo systemctl enable --quiet "$UNIT"

# Started as root, the files were created as root; hand them to the service user.
if [ "$EUID" -eq 0 ] && [ "$RUN_USER" != "root" ]; then
    chown -R "$RUN_USER" "$HOME_DIR"
fi

log_step "Starting OpenScanner AI Server $RELEASE_VERSION..."
# apply_update.sh keeps the current app/ as app.prev and rolls back to it if the
# new release does not stay up.
if ! sudo bash "$NEW/scripts/apply_update.sh" "$NEW" "$HOME_DIR" "$UNIT"; then
    # The previous release is back in app/; give it back its settings too.
    if [ -n "$CONFIG_BACKUP" ]; then cp "$CONFIG_BACKUP" "$CONFIG"; else rm -f "$CONFIG"; fi
    log_error "The server did not start. Check: journalctl -u $UNIT -n 100"
    exit 1
fi
SWITCHED=true
rm -f "$CONFIG_BACKUP"

if [ -f "$LEGACY_SERVICE_FILE" ]; then
    # Remove the pre-rename unit so two services never compete for the same port.
    log_info "Removing the old $LEGACY_UNIT service..."
    sudo systemctl disable --quiet "$LEGACY_UNIT" 2> /dev/null || true
    sudo rm -f "$LEGACY_SERVICE_FILE"
    sudo systemctl daemon-reload
fi
log_success "Service is running."

IP_ADDR=$(hostname -I 2> /dev/null | awk '{print $1}' || true)
echo ""
echo "================================================="
log_success "OpenScanner AI Server $RELEASE_VERSION installed"
echo "================================================="
echo -e "   ${BOLD}Model:${NC}   $WHISPER_MODEL ($([ "$USE_GPU" = true ] && echo GPU || echo CPU))"
if [ "$SKIP_OLLAMA" = false ]; then
    echo -e "   ${BOLD}LLM:${NC}     $LLM_MODEL ($OLLAMA_URL)"
fi
echo -e "   ${BOLD}Port:${NC}    $PORT"
echo -e "   ${BOLD}Status:${NC}  systemctl status $UNIT"
echo -e "   ${BOLD}Logs:${NC}    journalctl -u $UNIT -f"
echo -e "   ${BOLD}Health:${NC}  http://${IP_ADDR:-localhost}:$PORT/health"
echo -e "   ${BOLD}Config:${NC}  $CONFIG"
echo -e "   ${BOLD}Update:${NC}  curl -fsSL $INSTALL_URL | bash"
echo ""
echo -e "   To connect from OpenScanner, set the remote URL to:"
echo -e "   ${BOLD}http://${IP_ADDR:-localhost}:$PORT${NC}"
if [ -n "$LEGACY_ROOT" ]; then
    echo ""
    log_info "Settings and models now live in $HOME_DIR. The old checkout at $LEGACY_ROOT is no longer used and can be deleted."
fi
echo "================================================="
