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
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=false
