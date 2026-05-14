# WSL2Compactor

WSL2Compactor is an interactive Windows terminal app for compacting WSL2 `ext4.vhdx` files.

It scans detected WSL2 distros, lets you choose targets with terminal prompts, runs `fstrim`, shuts WSL down, compacts the selected VHDX files, and prints the bytes saved.

## Features

- Detects WSL2 distributions from `HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss`.
- Compacts detected WSL2 `ext4.vhdx` files without touching Linux files directly.
- Shows progress, elapsed time, estimated remaining time, start time, and end time.
- Uses `virtdisk.dll` / `CompactVirtualDisk` as the default backend.
- Falls back to `diskpart compact vdisk` when the VirtDisk API fails.
- Offers `Optimize-VHD` only when it is already available.
- Saves logs to `%LocalAppData%\WSL2Compactor\Logs`.
- Requires administrator privileges.

## Download

Download `WSL2Compactor-win-x64.exe` from the [latest GitHub Release](https://github.com/rnlcrosoft/WSL2Compactor/releases/latest), open Windows Terminal or PowerShell, and run it.

The executable requests administrator privileges automatically. .NET Runtime installation is not required.

The `.sha256` file is optional and can be used to verify the downloaded executable.

## Build

Requirements:

- .NET 10 SDK
- Windows 10/11 x64 target

From the repository root:

```bash
dotnet publish src/WSL2Compactor/WSL2Compactor.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true \
  -p:PublishTrimmed=false \
  -p:PublishReadyToRun=false
```

You can also run:

```bash
./scripts/publish-win-x64.sh
```

or, from PowerShell:

```powershell
.\scripts\publish-win-x64.ps1
```

## License

MIT
