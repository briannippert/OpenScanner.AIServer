#!/bin/bash
# uninstall.sh — remove the OpenScanner AI Server service and application.
#
#   /opt/openscanner-ai/app/scripts/uninstall.sh [--purge]
#
# By default your settings and Whisper models under /opt/openscanner-ai are kept,
# so a later install picks up where you left off. --purge deletes the whole
# install root as well.
#
# System packages, and Ollama with its model cache, are left alone: other
# applications may use them.
set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; BLUE='\033[0;34m'; NC='\033[0m'
log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_success() { echo -e "${GREEN}[OK] $1${NC}"; }
log_error() { echo -e "${RED}[ERROR] $1${NC}" >&2; }

HOME_DIR="${OPENSCANNER_AI_HOME:-/opt/openscanner-ai}"
SYSTEMD_DIR="${OPENSCANNER_AI_SYSTEMD_DIR:-/etc/systemd/system}"

PURGE=false
for arg in "$@"; do
    case "$arg" in
        --purge) PURGE=true ;;
        -h|--help) sed -n '2,11p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) log_error "Unknown option: $arg"; exit 2 ;;
    esac
done

if [ "$EUID" -ne 0 ]; then
    if command -v sudo > /dev/null; then
        # Run from a copy: the script lives inside the directory it is about to remove.
        TMP_COPY=$(mktemp)
        cat "${BASH_SOURCE[0]}" > "$TMP_COPY"
        exec sudo OPENSCANNER_AI_HOME="$HOME_DIR" OPENSCANNER_AI_SYSTEMD_DIR="$SYSTEMD_DIR" bash "$TMP_COPY" "$@"
    fi
    log_error "Please run as root."
    exit 1
fi

# openscanner-whisper is the unit name used before the rename to OpenScanner.AIServer.
# Ask systemd directly rather than grepping a listing: under `set -o pipefail`,
# `systemctl list-unit-files | grep -q` fails whenever grep matches early.
REMOVED=false
for unit in openscanner-ai openscanner-whisper; do
    service_file="$SYSTEMD_DIR/$unit.service"
    if [ -f "$service_file" ] || systemctl is-active --quiet "$unit" || systemctl is-enabled --quiet "$unit" 2> /dev/null; then
        log_info "Stopping and disabling $unit..."
        systemctl stop "$unit" || true
        systemctl disable --quiet "$unit" 2> /dev/null || true
    fi
    if [ -f "$service_file" ]; then
        rm -f "$service_file"
        REMOVED=true
        log_info "Removed $service_file."
    fi
done
if [ "$REMOVED" = true ]; then
    systemctl daemon-reload
else
    log_info "No service was installed."
fi

case "$HOME_DIR" in
    ""|/|/opt|/usr|/home|/root) log_error "Refusing to remove files under '$HOME_DIR'."; exit 1 ;;
esac

if [ "$PURGE" = true ]; then
    log_info "Removing $HOME_DIR (including settings and models)..."
    rm -rf "$HOME_DIR"
    log_success "OpenScanner AI Server and all of its files removed."
else
    rm -rf "$HOME_DIR/app" "$HOME_DIR/app.prev" "$HOME_DIR/app.failed" "$HOME_DIR/staging"
    log_success "OpenScanner AI Server removed."
    if [ -d "$HOME_DIR" ]; then
        echo "Your settings and Whisper models are kept in $HOME_DIR."
        echo "Delete them too with: sudo rm -rf $HOME_DIR"
    fi
fi
