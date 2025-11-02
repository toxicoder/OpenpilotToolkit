#!/usr/bin/env bash
set -euo pipefail

DOTNET_ROOT="$HOME/.dotnet"
export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$HOME/.dotnet/tools:$PATH"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# Fix PWD for Cap'n Proto
unset PWD
cd "$REPO_ROOT"

PLATFORM="${1:-win-x64}"
PROJECT="OpenpilotToolkit.AvaloniaUI/OpenpilotToolkit.AvaloniaUI.csproj"

echo "================================================"
echo "Restoring OpenpilotToolkit Dependencies for $PLATFORM"
echo "================================================"

if [[ ! -f "$PROJECT" ]]; then
    echo "❌ Error: Project not found: $PROJECT"
    exit 1
fi

echo ""
echo "Restoring $PROJECT..."

dotnet restore "$PROJECT" \
    --runtime "$PLATFORM" \
    /p:EnableWindowsTargeting=true \
    --verbosity quiet

echo ""
echo "✅ All dependencies restored successfully!"
echo ""
