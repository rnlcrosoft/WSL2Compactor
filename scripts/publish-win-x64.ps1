param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

dotnet publish src/WSL2Compactor/WSL2Compactor.csproj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=true `
    -p:PublishReadyToRun=false

$exePath = "src/WSL2Compactor/bin/$Configuration/net10.0-windows/win-x64/publish/WSL2Compactor.exe"
$maxSize = 26214400

if (-not (Test-Path $exePath)) {
    throw "Published executable not found: $exePath"
}

$actualSize = (Get-Item $exePath).Length
if ($actualSize -gt $maxSize) {
    throw "Published executable is too large: $actualSize bytes. Limit: $maxSize bytes."
}

Write-Host "Published executable size: $actualSize bytes"
