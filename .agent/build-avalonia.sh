#!/bin/bash
set -e

# Set configuration to Release if not provided
CONFIGURATION=${1:-Release}
PROJECT_NAME="OpenpilotToolkit.AvaloniaUI"
OUTPUT_DIR="OpenpilotToolkit.AvaloniaUI/bin/$CONFIGURATION/net9.0/osx-x64"
APP_BUNDLE_DIR="$OUTPUT_DIR/$PROJECT_NAME.app"

# Publish the AvaloniaUI project for macOS
echo "Publishing AvaloniaUI for macOS in $CONFIGURATION configuration..."
dotnet publish OpenpilotToolkit.AvaloniaUI/OpenpilotToolkit.AvaloniaUI.csproj --configuration "$CONFIGURATION" --runtime osx-x64

# Create the .app bundle structure
echo "Creating .app bundle..."
mkdir -p "$APP_BUNDLE_DIR/Contents/MacOS"
mkdir -p "$APP_BUNDLE_DIR/Contents/Resources"

# Move the published files into the .app bundle
mv $OUTPUT_DIR/publish/* "$APP_BUNDLE_DIR/Contents/MacOS/"

# Copy the Info.plist
cp OpenpilotToolkit.AvaloniaUI/Info.plist "$APP_BUNDLE_DIR/Contents/"

# Make the executable runnable
chmod +x "$APP_BUNDLE_DIR/Contents/MacOS/$PROJECT_NAME"

echo "Build complete. Application bundle can be found at: $APP_BUNDLE_DIR"
