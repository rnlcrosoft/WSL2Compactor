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
