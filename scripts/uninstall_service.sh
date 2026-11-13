#!/bin/bash
set -e

# =====================================================
# OpenScanner AI Server Uninstaller
# =====================================================

RED='\033[0;31m'
GREEN='\033[0;32m'
BLUE='\033[0;34m'
NC='\033[0m'
BOLD='\033[1m'

log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_step() { echo -e "\n${BLUE}${BOLD}==> $1${NC}"; }
log_success() { echo -e "${GREEN}[OK] $1${NC}"; }

if [ "$EUID" -eq 0 ]; then
  echo -e "${RED}[ERROR]${NC} Please run as a regular user (NOT root)."
  exit 1
fi

# openscanner-whisper is the unit name used before the rename to OpenScanner.AIServer.
REMOVED=false
for unit in openscanner-ai openscanner-whisper; do
    log_step "Removing $unit..."
    if systemctl is-active --quiet "$unit"; then
        sudo systemctl stop "$unit"
        log_info "Service stopped."
    fi
    if systemctl is-enabled --quiet "$unit" 2>/dev/null; then
        sudo systemctl disable "$unit"
        log_info "Service disabled."
    fi
    if [ -f "/etc/systemd/system/$unit.service" ]; then
        sudo rm "/etc/systemd/system/$unit.service"
        REMOVED=true
        log_info "Service file removed."
    fi
done
if [ "$REMOVED" = true ]; then
    sudo systemctl daemon-reload
fi

echo ""
log_success "OpenScanner AI Server service uninstalled."
log_info "Project files have not been removed. Delete manually if desired."
