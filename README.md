# WSL2Compactor

WSL2Compactor is an interactive Windows app for compacting WSL2 `ext4.vhdx` files.

It automates the Windows-side compaction flow for WSL2 distributions.

## What It Does

1. Reads WSL distribution metadata from `HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss`.
2. Selects WSL2 distributions with an existing `ext4.vhdx`.
3. Runs `wsl.exe -d <distro> --user root fstrim -av`.
4. Runs `wsl.exe --shutdown`.
5. Compacts the selected VHDX file.

## Why These Steps

`fstrim` asks the Linux filesystem to discard unused blocks. That gives Windows useful block-discard information before the VHDX file is compacted.

`wsl.exe --shutdown` is required because the VHDX file must not be held open by WSL while Windows compacts it.

The default backend is `virtdisk.dll` / `CompactVirtualDisk` with `COMPACT_VIRTUAL_DISK_FLAG_NO_ZERO_SCAN`. This is the fast path for the app because `fstrim` already performs the Linux-side discard step, so a full zero scan is usually unnecessary for WSL2 compaction.

If the VirtDisk API fails, the app falls back to `diskpart compact vdisk`. `Optimize-VHD` is offered only when it already exists on the machine.

The size columns use Windows allocated disk usage for the primary before/after/saved values. The VHDX file length is shown separately as `VHDX size` because sparse VHDX compaction is about freeing allocated host-disk space, not just changing the file's logical length.

> [!NOTE]
> `Optimize-VHD` is installed with Hyper-V tooling. On Windows Home, Hyper-V is not exposed by default. If you want to make the Hyper-V `Optimize-VHD` backend available through an unofficial DISM package route, this gist describes one approach: [Hyper-V in Windows 10 and Windows 11 Home Edition](https://gist.github.com/HimDek/6edde284203a620745fad3f762be603b). Expect a Windows reboot after changing Hyper-V features.

Logs are written to `%LocalAppData%\WSL2Compactor\Logs`.

Administrator privileges are required for Windows-side VHDX compaction.

## Download

Download `WSL2Compactor-win-x64.exe` from the [latest GitHub Release](https://github.com/rnlcrosoft/WSL2Compactor/releases/latest) and run it.

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
  -p:PublishTrimmed=true \
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
