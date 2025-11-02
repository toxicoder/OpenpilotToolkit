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
CONFIGURATION="Release"
PROJECT="OpenpilotToolkit.AvaloniaUI/OpenpilotToolkit.AvaloniaUI.csproj"

echo "================================================"
echo "Building OpenpilotToolkit ($CONFIGURATION) for $PLATFORM"
echo "================================================"

if [[ ! -f "$PROJECT" ]]; then
    echo "❌ Error: Project not found: $PROJECT"
    exit 1
fi

echo ""
echo "Building $PROJECT..."
echo ""

dotnet build "$PROJECT" \
    --configuration "$CONFIGURATION" \
    --runtime "$PLATFORM" \
    --no-restore \
    /p:EnableWindowsTargeting=true \
    --nologo

OUTPUT_DIR="OpenpilotToolkit.AvaloniaUI/bin/$CONFIGURATION/net9.0/$PLATFORM"
EXE_PATH="$OUTPUT_DIR/OpenpilotToolkit.AvaloniaUI"

if [[ "$PLATFORM" == "win-x64" ]]; then
    EXE_PATH="$OUTPUT_DIR/OpenpilotToolkit.AvaloniaUI.exe"
fi

echo ""
echo "================================================"
echo "✅ Build completed successfully!"
echo "================================================"
echo ""
echo "Output: $EXE_PATH"
echo ""
