#!/usr/bin/env bash
# apply_webidentity.sh — Linux/macOS wrapper for apply_webidentity.py.
# Usage:
#    ./apply_webidentity.sh
#    ./apply_webidentity.sh /home/me/Source-X
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if ! command -v python3 >/dev/null 2>&1; then
    echo "ERROR: python3 is not installed." >&2
    echo "  On Debian/Ubuntu:  sudo apt install python3" >&2
    echo "  On macOS:          brew install python   (or install from python.org)" >&2
    exit 1
fi

exec python3 "${SCRIPT_DIR}/apply_webidentity.py" "$@"
