param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

dotnet publish src/WslAutoCompact/WslAutoCompact.csproj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=false
