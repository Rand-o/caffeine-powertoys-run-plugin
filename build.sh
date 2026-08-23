#!/usr/bin/env bash
# Build the Caffeine PowerToys Run plugin (Linux cross-compile) and
# assemble dist/Caffeine/ + dist/Caffeine.zip from the x64 output.
set -euo pipefail

cd "$(dirname "$0")"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
if [ -d "$HOME/.dotnet" ]; then
  export PATH="$HOME/.dotnet:$PATH"
fi

PROJ=Community.PowerToys.Run.Plugin.Caffeine.csproj
DIST=dist
PLUGIN_DIR="$DIST/Caffeine"
X64_OUT=bin/x64/Release/net9.0-windows

API_DLLS=(
  Wox.Plugin.dll
  Wox.Infrastructure.dll
  PowerToys.Common.UI.dll
  PowerToys.ManagedCommon.dll
  PowerToys.Settings.UI.Lib.dll
)

echo "==> Building x64 (Release)"
dotnet build "$PROJ" -c Release -p:Platform=x64

echo "==> Building ARM64 (Release, compile-check only)"
dotnet build "$PROJ" -c Release -p:Platform=ARM64

echo "==> Assembling $PLUGIN_DIR from x64 output"
rm -rf "$DIST"
mkdir -p "$PLUGIN_DIR"

cp "$X64_OUT/Community.PowerToys.Run.Plugin.Caffeine.dll" "$PLUGIN_DIR/"
cp plugin.json "$PLUGIN_DIR/"
cp -r Images "$PLUGIN_DIR/"

for dll in "${API_DLLS[@]}"; do
  if [ ! -f "$X64_OUT/$dll" ]; then
    echo "ERROR: expected bundled API DLL missing from build output: $dll" >&2
    exit 1
  fi
  cp "$X64_OUT/$dll" "$PLUGIN_DIR/"
done

# No .pdb files in the distributed folder (explicit copies above guarantee it)
if find "$PLUGIN_DIR" -name '*.pdb' | grep -q .; then
  echo "ERROR: .pdb files found in $PLUGIN_DIR" >&2
  exit 1
fi

echo "==> Creating $DIST/Caffeine.zip"
(cd "$DIST" && zip -r -q Caffeine.zip Caffeine)

echo "==> Done. Contents of $PLUGIN_DIR:"
ls -la "$PLUGIN_DIR"
