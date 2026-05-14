#!/usr/bin/env bash
set -euo pipefail

dotnet publish src/WslAutoCompact/WslAutoCompact.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true \
  -p:PublishTrimmed=false \
  -p:PublishReadyToRun=false
