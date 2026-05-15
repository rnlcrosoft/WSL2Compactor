# WSL2Compactor

WSL2Compactor is an interactive Windows console application for compacting WSL2 `ext4.vhdx` files.

It guides the WSL and Windows compaction steps in order.

```text
Deleted files inside WSL do not automatically shrink ext4.vhdx.

+----------------------------------+-------------------------+----------------+
| State                            | Windows C: free / total | Host allocated |
+----------------------------------+-------------------------+----------------+
| Initial state                    | 500 GiB / 1000 GiB      | 500 GiB        |
| [WSL] Create 100 GiB test data   | 400 GiB / 1000 GiB      | 600 GiB        |
| [WSL] Delete that test data      | 400 GiB / 1000 GiB      | 600 GiB        |
| [WSL2Compactor] Compact VHDX     | 500 GiB / 1000 GiB      | 500 GiB        |
+----------------------------------+-------------------------+----------------+

Result: 100 GiB of deleted WSL data returned to Windows.
```

## Download

Download `WSL2Compactor-win-x64.exe` from the [latest GitHub Release](https://github.com/rnlcrosoft/WSL2Compactor/releases/latest) and run it as Administrator.

Logs are written to `%LocalAppData%\WSL2Compactor\Logs`.

## How It Works

- Finds WSL2 distributions with an existing `ext4.vhdx`.
- Shows Linux filesystem usage, Windows host allocation, and VHDX file length.
- Runs `fstrim` inside selected distros.
- Runs `wsl.exe --shutdown` so Windows can compact the VHDX file.
- Compacts with the Windows `VirtDisk API`.

The app offers two compact modes:

- `No zero scan`: uses `COMPACT_VIRTUAL_DISK_FLAG_NO_ZERO_SCAN`.
- `Zero scan`: calls `CompactVirtualDisk` without that flag.

`Optimize-VHD` and `DiskPart` are not automatic backends.

## Displayed Values

| Column | Meaning |
| --- | --- |
| `Linux used` | `df`-style filesystem space used inside the distro. |
| `Ext4 overhead` | Exact ext4 structural overhead when available; otherwise `-`. |
| `Linux footprint` | `Linux used + Ext4 overhead`, shown only when both values are available. |
| `Host allocated` | Windows host disk space allocated by `ext4.vhdx`, read with `GetCompressedFileSizeW`. |
| `File length` | Logical Windows file length of `ext4.vhdx`. |
| `Saved` | Actual host allocation delta after compaction: `max(0, host allocated before - host allocated after)`. |

The app does not show a pre-run savings estimate because that cannot be computed from `Linux used`, `Host allocated`, and `File length` alone.

## Build

Requirements:

- .NET 10 SDK
- Make
- Windows 10/11 x64 target

The app is a Windows x64 executable. The Makefile can be used from WSL or from a Unix-like shell with Make on Windows.

From the repository root:

```sh
make build
make publish
```

The published executable is written to `.build/publish/WSL2Compactor.exe`.

To publish, copy to the Windows Desktop, and launch the app as Administrator:

```sh
make run
```

For release checks, run:

```sh
make verify
```

## License

MIT
