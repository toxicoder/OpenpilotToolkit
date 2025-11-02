#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIGURATION="${1:-Release}"
PLATFORM="${2:-win-x64}"

echo "================================================"
echo "Quick Build: Restore + Build ($CONFIGURATION) for $PLATFORM"
echo "================================================"

bash "$SCRIPT_DIR/restore.sh" "$PLATFORM" || exit 1

echo ""

bash "$SCRIPT_DIR/build.sh" "$CONFIGURATION" "$PLATFORM" || exit 1

echo ""
echo "✅ Quick build complete!"
echo ""
