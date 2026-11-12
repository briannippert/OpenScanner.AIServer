#!/bin/bash
set -e

# =====================================================
# OpenScanner WhisperServer Installer
# =====================================================
# Installs system dependencies, builds whisper.cpp,
# downloads a Whisper model, builds the .NET server,
# and configures a systemd service.
# =====================================================

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'
BOLD='\033[1m'

log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_step() { echo -e "\n${BLUE}${BOLD}==> $1${NC}"; }
log_success() { echo -e "${GREEN}[OK] $1${NC}"; }
log_warn() { echo -e "${YELLOW}[WARN] $1${NC}"; }
log_error() { echo -e "${RED}[ERROR] $1${NC}"; }

# Parse Arguments
DEPS_ONLY=false
REBUILD_WHISPER=false
PORT=""
SKIP_UPDATE=false
for arg in "$@"; do
  case $arg in
    --skip-update)
      SKIP_UPDATE=true
      shift
      ;;
    --deps-only)
      DEPS_ONLY=true
      shift
      ;;
    --port=*)
      PORT="${arg#*=}"
      shift
      ;;
    --rebuild-whisper)
      REBUILD_WHISPER=true
      shift
      ;;
  esac
done

# Check NOT Root
if [ "$EUID" -eq 0 ]; then
  log_error "Please run as a regular user (NOT root)."
  log_error "The script will ask for sudo password when needed."
  exit 1
fi

# Ensure sudo is available
if ! command -v sudo &> /dev/null; then
    log_error "This script requires 'sudo' to install system dependencies."
    exit 1
fi

# Refresh sudo credentials upfront
sudo -v

PROJECT_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)

log_step "Initializing Setup"
log_info "Project Root: $PROJECT_ROOT"
log_info "User: $USER ($HOME)"

# ----------------------------------------------------------------
# 1. Stop Existing Service
# ----------------------------------------------------------------
log_step "Stopping existing service..."
if systemctl is-active --quiet openscanner-whisper; then
    sudo systemctl stop openscanner-whisper
    log_info "Service stopped."
else
    log_info "Service was not running."
fi

# ----------------------------------------------------------------
# 2. Update Code
# ----------------------------------------------------------------
log_step "Updating Repository..."
if [ "$SKIP_UPDATE" = true ]; then
    log_info "Using local code (--skip-update)."
elif git remote get-url origin &> /dev/null; then
    if git pull origin main; then
        log_success "Code pulled successfully."
    else
        log_warn "Git pull failed. Continuing with local files..."
    fi
else
    log_warn "No git remote found. Skipping update."
fi

# ----------------------------------------------------------------
# 3. Check .NET SDK
# ----------------------------------------------------------------
log_step "Checking Environment..."

INSTALL_DOTNET=false
if ! command -v dotnet &> /dev/null; then
    log_info ".NET SDK not found."
    INSTALL_DOTNET=true
else
    DOTNET_VER=$(dotnet --version)
    MAJOR_VER=$(echo "$DOTNET_VER" | cut -d. -f1)
    if [ "$MAJOR_VER" -lt 10 ]; then
        log_info "Found .NET SDK $DOTNET_VER, but .NET 10 is required."
        INSTALL_DOTNET=true
    else
        log_success "Found .NET SDK: $DOTNET_VER"
    fi
fi

if [ "$INSTALL_DOTNET" = true ]; then
    log_info "Attempting to install .NET 10 SDK..."

    if command -v apt-get &> /dev/null; then
        wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
        sudo dpkg -i packages-microsoft-prod.deb
        rm packages-microsoft-prod.deb

        sudo apt-get update -qq || log_warn "apt-get update encountered errors. Attempting to continue..."
        sudo apt-get install -y -qq dotnet-sdk-10.0
        log_success ".NET 10 SDK installed."
    else
         log_warn "Could not install .NET SDK automatically. Please install .NET 10 SDK manually."
    fi
fi

# ----------------------------------------------------------------
# 4. System Dependencies
# ----------------------------------------------------------------
log_step "Installing System Libraries..."

sudo apt-get update -qq || log_warn "apt-get update encountered errors. Attempting to continue..."
sudo apt-get install -y -qq git cmake build-essential ffmpeg jq > /dev/null
log_success "Libraries installed."

# Install NVIDIA CUDA toolkit if GPU is present but nvcc is missing
if command -v nvidia-smi &> /dev/null && nvidia-smi &> /dev/null; then
    if ! command -v nvcc &> /dev/null; then
        log_info "NVIDIA GPU detected but CUDA toolkit not installed. Installing..."
        sudo apt-get install -y -qq nvidia-cuda-toolkit > /dev/null
        log_success "NVIDIA CUDA toolkit installed."
    fi
fi

# ----------------------------------------------------------------
# 5. Whisper Model Selection
# ----------------------------------------------------------------
log_step "Whisper Model Selection"

APPSETTINGS="$PROJECT_ROOT/appsettings.json"
CURRENT_MODEL=$(jq -r '.Whisper.DefaultModel // "large-v3-turbo-q5_0"' "$APPSETTINGS" 2>/dev/null || echo "large-v3-turbo-q5_0")

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
echo -e "  Current model: ${BOLD}$CURRENT_MODEL${NC}"
echo ""

read -r -p "$(echo -e "${BLUE}[INFO]${NC} Select a model [1-6] (press Enter to keep ${BOLD}$CURRENT_MODEL${NC}): ")" MODEL_CHOICE

case "$MODEL_CHOICE" in
    1) WHISPER_MODEL="tiny.en" ;;
    2) WHISPER_MODEL="base.en" ;;
    3) WHISPER_MODEL="small.en" ;;
    4) WHISPER_MODEL="medium.en" ;;
    5) WHISPER_MODEL="large-v3-turbo-q5_0" ;;
    6) WHISPER_MODEL="large-v3" ;;
    *) WHISPER_MODEL="$CURRENT_MODEL" ;;
esac

log_info "Using model: $WHISPER_MODEL"

# ----------------------------------------------------------------
# 5b. Port Selection
# ----------------------------------------------------------------
if [ -z "$PORT" ]; then
    CURRENT_PORT=$(jq -r '.Urls // empty' "$APPSETTINGS" 2>/dev/null | grep -oP ':\K[0-9]+' || echo "8090")
    [ -z "$CURRENT_PORT" ] && CURRENT_PORT="8090"

    echo ""
    read -r -p "$(echo -e "${BLUE}[INFO]${NC} Port to run the server on (press Enter to keep ${BOLD}$CURRENT_PORT${NC}): ")" PORT_CHOICE
    PORT="${PORT_CHOICE:-$CURRENT_PORT}"
fi

log_info "Using port: $PORT"

# ----------------------------------------------------------------
# 6. Whisper.cpp Setup
# ----------------------------------------------------------------
log_step "Checking Whisper.cpp..."

WHISPER_DIR="$PROJECT_ROOT/whisper.cpp"

# Clone if the directory is missing OR incomplete (e.g. an empty placeholder dir
# left in the repo, or a partial checkout with no CMakeLists.txt). Removing first
# lets `git clone` succeed into what would otherwise be a non-empty directory.
if [ ! -f "$WHISPER_DIR/CMakeLists.txt" ]; then
    log_info "whisper.cpp missing or incomplete; cloning..."
    rm -rf "$WHISPER_DIR"
    git clone https://github.com/ggerganov/whisper.cpp.git "$WHISPER_DIR"
fi

# Detect NVIDIA GPU for CUDA acceleration
CMAKE_EXTRA_ARGS=""
if command -v nvidia-smi &> /dev/null && nvidia-smi &> /dev/null; then
    GPU_NAME=$(nvidia-smi --query-gpu=name --format=csv,noheader 2>/dev/null | head -1)
    log_success "NVIDIA GPU detected: $GPU_NAME"

    if command -v nvcc &> /dev/null; then
        log_info "CUDA toolkit found. Enabling GPU acceleration."
        CMAKE_EXTRA_ARGS="-DGGML_CUDA=ON"
    else
        log_warn "NVIDIA GPU detected but CUDA toolkit (nvcc) not found."
        log_info "Install the CUDA toolkit for GPU acceleration:"
        log_info "  sudo apt-get install nvidia-cuda-toolkit"
        log_info "Falling back to CPU-only build."
    fi
else
    log_info "No NVIDIA GPU detected. Using CPU-only build."
fi

if [ "$REBUILD_WHISPER" = true ] && [ -d "$WHISPER_DIR/build" ]; then
    log_info "Removing existing whisper.cpp build (--rebuild-whisper)..."
    rm -rf "$WHISPER_DIR/build"
fi

# Auto-detect CUDA mismatch: GPU available but binary built without CUDA
if [ -f "$WHISPER_DIR/build/bin/whisper-server" ] && [ -n "$CMAKE_EXTRA_ARGS" ]; then
    if ! ldd "$WHISPER_DIR/build/bin/whisper-server" 2>/dev/null | grep -q 'libcuda'; then
        log_warn "Existing whisper-server was built without CUDA, but GPU acceleration is available."
        log_info "Rebuilding whisper.cpp with CUDA support..."
        rm -rf "$WHISPER_DIR/build"
    fi
fi

# We need the resident whisper-server binary (built by default with the examples).
if [ ! -f "$WHISPER_DIR/build/bin/whisper-server" ]; then
    log_info "Building whisper.cpp${CMAKE_EXTRA_ARGS:+ (with CUDA)}..."
    cd "$WHISPER_DIR"
    cmake -B build $CMAKE_EXTRA_ARGS
    cmake --build build --config Release -j$(nproc)
    log_success "Whisper.cpp built."
fi

# Download model (and verify it isn't a truncated/corrupt partial download — an
# incomplete .bin left by an interrupted download would otherwise be skipped
# forever because the file "exists", and whisper-server would fail to load it).
MODEL_BIN="$WHISPER_DIR/models/ggml-$WHISPER_MODEL.bin"

download_model() {
    log_info "Downloading Whisper model ($WHISPER_MODEL)..."
    ( cd "$WHISPER_DIR" && bash ./models/download-ggml-model.sh "$WHISPER_MODEL" )
}

verify_model() {
    # Loading the model reads every tensor, so a truncated file fails here. Run on
    # CPU (-ng) with a 1s decode so this is a fast integrity check, not a benchmark,
    # and independent of available VRAM.
    "$WHISPER_DIR/build/bin/whisper-cli" -m "$MODEL_BIN" -f "$WHISPER_DIR/samples/jfk.wav" \
        -ng -nt -d 1000 > /dev/null 2>&1
}

[ ! -f "$MODEL_BIN" ] && download_model

if [ -f "$WHISPER_DIR/build/bin/whisper-cli" ] && [ -f "$WHISPER_DIR/samples/jfk.wav" ]; then
    log_info "Verifying model integrity..."
    if ! verify_model; then
        log_warn "Model failed to load (incomplete/corrupt download). Re-downloading..."
        rm -f "$MODEL_BIN"
        download_model
        if ! verify_model; then
            log_error "Model still fails to load after re-download. Check free disk space and network."
            exit 1
        fi
    fi
    log_success "Whisper model ready and verified."
else
    log_success "Whisper model downloaded (skipped load check: whisper-cli/sample unavailable)."
fi
cd "$PROJECT_ROOT"

# ----------------------------------------------------------------
# 7. Size the resident-model pool to the GPU and selected model
# ----------------------------------------------------------------
log_step "Sizing model pool..."

# Each resident whisper-server process holds a full copy of the model plus compute
# buffers. Estimate per-instance VRAM from the model file size (weights ~1.35x on
# GPU + a fixed buffer allowance), then fit as many process "slots" as the GPU has.
MODEL_FILE="$WHISPER_DIR/models/ggml-$WHISPER_MODEL.bin"
MODEL_MB=0
if [ -f "$MODEL_FILE" ]; then
    MODEL_MB=$(du -m "$MODEL_FILE" | cut -f1)
fi
PER_INSTANCE_MB=$(( MODEL_MB * 135 / 100 + 700 ))

# Detect total VRAM (MB) on the primary GPU.
VRAM_MB=0
if command -v nvidia-smi &> /dev/null && nvidia-smi &> /dev/null; then
    VRAM_MB=$(nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>/dev/null | head -1 | tr -dc '0-9')
    [ -z "$VRAM_MB" ] && VRAM_MB=0
fi

# Reserve memory for the co-located address model. Set to 0 when Ollama runs
# elsewhere or address extraction is unused. This is a budget, not enforcement.
ADDRESS_RESERVE_MB=$(jq -r '.Address.ReservedVramMb // 3500' "$APPSETTINGS")
if ! [[ "$ADDRESS_RESERVE_MB" =~ ^[0-9]+$ ]] || [ "${#ADDRESS_RESERVE_MB}" -gt 6 ]; then
    log_error "Address.ReservedVramMb must be an integer from 0 to 999999."
    exit 1
fi
ADDRESS_RESERVE_MB=$((10#$ADDRESS_RESERVE_MB))

# Only size against VRAM when we actually built with CUDA and have a reading.
if [ -n "$CMAKE_EXTRA_ARGS" ] && [ "$VRAM_MB" -gt 0 ] && [ "$PER_INSTANCE_MB" -gt 0 ]; then
    USE_GPU=true
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
    USE_GPU=false
    INSTANCES=1
    MAXMODELS=1
    log_info "No CUDA GPU sizing available; using a single resident instance."
fi

log_step "Updating Configuration..."

WHISPER_BIN="$WHISPER_DIR/build/bin/whisper-server"
MODELS_DIR="$WHISPER_DIR/models"

UPDATED=$(jq \
    --arg bin "$WHISPER_BIN" \
    --arg modelsDir "$MODELS_DIR" \
    --arg defaultModel "$WHISPER_MODEL" \
    --argjson instances "$INSTANCES" \
    --argjson maxModels "$MAXMODELS" \
    --argjson useGpu "$USE_GPU" \
    '.Whisper.WhisperServerBinary = $bin | .Whisper.ModelsDir = $modelsDir | .Whisper.DefaultModel = $defaultModel | .Whisper.InstancesPerModel = $instances | .Whisper.MaxResidentModels = $maxModels | .Whisper.UseGpu = $useGpu' \
    "$APPSETTINGS")
echo "$UPDATED" > "$APPSETTINGS"

log_success "Configuration updated:"
log_info "  Binary:       $WHISPER_BIN"
log_info "  Models dir:   $MODELS_DIR"
log_info "  Default:      $WHISPER_MODEL"
log_info "  Instances:    $INSTANCES per model  (max $MAXMODELS resident model(s), GPU=$USE_GPU)"

if [ "$DEPS_ONLY" = true ]; then
    log_success "Dependencies installed. Skipping build and service installation (--deps-only)."
    exit 0
fi

# ----------------------------------------------------------------
# 8. Build Application
# ----------------------------------------------------------------
log_step "Building Application..."

cd "$PROJECT_ROOT"
if dotnet build -c Release -o bin/Release/net10.0/publish; then
    log_success "Server built successfully."
else
    log_error "Server build failed."
    exit 1
fi

# ----------------------------------------------------------------
# 9. Configure Systemd Service
# ----------------------------------------------------------------
log_step "Configuring Systemd Service..."

SERVICE_FILE="/etc/systemd/system/openscanner-whisper.service"
NET_EXEC="$PROJECT_ROOT/bin/Release/net10.0/publish/OpenScanner.WhisperServer"

chmod +x "$NET_EXEC"

TEMP_SERVICE_FILE=$(mktemp)
cat <<EOF > "$TEMP_SERVICE_FILE"
[Unit]
Description=OpenScanner Whisper Transcription Server
After=network.target

[Service]
Type=simple
User=$USER
WorkingDirectory=$PROJECT_ROOT
ExecStart=$NET_EXEC --urls "http://0.0.0.0:$PORT"
Restart=always
RestartSec=5
Environment=DOTNET_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://0.0.0.0:$PORT

[Install]
WantedBy=multi-user.target
EOF

sudo mv "$TEMP_SERVICE_FILE" "$SERVICE_FILE"
sudo chown root:root "$SERVICE_FILE"
sudo chmod 644 "$SERVICE_FILE"

sudo systemctl daemon-reload
sudo systemctl enable openscanner-whisper
sudo systemctl restart openscanner-whisper

# ----------------------------------------------------------------
# 10. Finalize
# ----------------------------------------------------------------
log_step "Finalizing..."
chmod +x "$PROJECT_ROOT"/scripts/*.sh

IP_ADDR=$(hostname -I | awk '{print $1}')

echo ""
echo "================================================="
log_success "Installation Complete!"
echo "================================================="
echo -e "   ${BOLD}Model:${NC}   $WHISPER_MODEL"
echo -e "   ${BOLD}Port:${NC}    $PORT"
echo -e "   ${BOLD}Status:${NC}  systemctl status openscanner-whisper"
echo -e "   ${BOLD}Logs:${NC}    journalctl -u openscanner-whisper -f"
echo -e "   ${BOLD}Health:${NC}  http://$IP_ADDR:$PORT/health"
echo ""
echo -e "   To connect from OpenScanner, set the remote URL to:"
echo -e "   ${BOLD}http://$IP_ADDR:$PORT${NC}"
echo "================================================="
