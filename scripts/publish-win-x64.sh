#!/usr/bin/env bash
set -euo pipefail

dotnet publish src/WSL2Compactor/WSL2Compactor.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true \
  -p:PublishTrimmed=true \
  -p:PublishReadyToRun=false

exe_path="src/WSL2Compactor/bin/Release/net10.0-windows/win-x64/publish/WSL2Compactor.exe"
max_size=26214400

if [[ ! -f "$exe_path" ]]; then
  echo "Published executable not found: $exe_path" >&2
  exit 1
fi

actual_size=$(stat -c%s "$exe_path")
if (( actual_size > max_size )); then
  echo "Published executable is too large: ${actual_size} bytes. Limit: ${max_size} bytes." >&2
  exit 1
fi

echo "Published executable size: ${actual_size} bytes"
