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

## Example: Reclaiming Deleted WSL Data

This is a small reproducible test that writes 30 GiB inside WSL, deletes it, then lets WSL2Compactor reclaim the host disk space.

Check Windows free space before the test:

```powershell
[math]::Round((Get-PSDrive C).Free / 1GB, 2)
```

Example output:

```text
96.48
```

Create a 30 GiB non-zero file inside WSL:

```bash
mkdir -p ~/wsl2compactor-test

dd if=/dev/zero bs=1M count=30720 status=progress \
  | openssl enc -aes-256-ctr -pass pass:wsl2compactor-test -pbkdf2 -nosalt \
  > ~/wsl2compactor-test/fill-30g.bin

sync
ls -lh ~/wsl2compactor-test/fill-30g.bin
df -h /
```

Delete the file before compacting:

```bash
rm -f ~/wsl2compactor-test/fill-30g.bin
sync
```

At this point, Windows free space can still be low because the WSL2 `ext4.vhdx` file has not been compacted yet. In one test run, Windows free space went from `96.48 GB` to `66.48 GB` after creating and deleting the 30 GiB file.

After running WSL2Compactor, the important log lines looked like this:

```text
INFO Ubuntu / fstrim /: 494.6 GiB (531035119616 bytes) trimmed on /dev/sdd
INFO Ubuntu / VirtDisk API / compact Disk usage before: 533.98 GiB; VHDX size: 533.98 GiB
INFO Ubuntu / VirtDisk API / VirtDisk CompactVirtualDisk completed
INFO Ubuntu / VirtDisk API / complete Disk usage after: 503.95 GiB; saved: 30.03 GiB; VHDX size: 503.95 GiB
INFO Ubuntu / VirtDisk API / complete Finished Ubuntu.
```

Windows free space after compaction:

```powershell
[math]::Round((Get-PSDrive C).Free / 1GB, 2)
```

Example output:

```text
96.51
```

The exact numbers will vary, but the expected result is that `Disk usage after` drops by roughly the deleted file size and Windows free space returns after compaction.

> [!CAUTION]
> Make sure the Windows drive has enough free space before creating large test files. A 30 GiB file is usually enough to demonstrate the behavior. Avoid filling the Windows drive to near-zero free space.

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
