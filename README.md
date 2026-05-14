# WSL Auto Compact

WSL Auto Compact is a small Windows GUI app for compacting WSL2 `ext4.vhdx` files.

It shows each step in a CLI-style log, runs `fstrim`, shuts WSL down, then compacts the selected VHDX files.

## Features

- Detects WSL2 distributions from `HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss`.
- Targets only existing `ext4.vhdx` files.
- Runs `wsl.exe -d <distro> --user root fstrim -av` before compacting.
- Runs `wsl.exe --shutdown` before touching the VHDX.
- Uses `virtdisk.dll` / `CompactVirtualDisk` as the default backend.
- Falls back to `diskpart compact vdisk` when the VirtDisk API fails.
- Offers `Optimize-VHD` only when it is already available.
- Monitors and closes Windows format prompts during compact operations.
- Saves logs to `%LocalAppData%\WSL Auto Compact\Logs`.
- Requires administrator privileges.

## Safety notes

Running this app stops WSL. Tools that depend on WSL, such as Docker Desktop, VS Code Remote, and open WSL terminals, may be interrupted.

During compact operations, the app watches for Windows format prompts and closes them automatically.

## Build

Requirements:

- .NET 10 SDK
- Windows 10/11 x64 target

From the repository root:

```bash
dotnet publish src/WslAutoCompact/WslAutoCompact.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=false \
  -p:PublishReadyToRun=false
```

The executable is created under:

```text
src/WslAutoCompact/bin/Release/net10.0-windows/win-x64/publish/
```

You can also run:

```bash
./scripts/publish-win-x64.sh
```

or, from PowerShell:

```powershell
.\scripts\publish-win-x64.ps1
```

## GitHub Release

Releases are automated by `.github/workflows/release.yml`.

After committing and pushing the release commit, create and push a version tag:

```bash
git tag v0.1.0
git push origin v0.1.0
```

The workflow publishes these release assets:

```text
WslAutoCompact-win-x64.exe
WslAutoCompact-win-x64.exe.sha256
```

You can also run the `Release` workflow manually from GitHub Actions and provide an existing tag, such as `v0.1.0`.

Manual local fallback with GitHub CLI:

```bash
./scripts/publish-win-x64.sh
mkdir -p artifacts/release
cp src/WslAutoCompact/bin/Release/net10.0-windows/win-x64/publish/WslAutoCompact.exe artifacts/release/WslAutoCompact-win-x64.exe
(cd artifacts/release && sha256sum WslAutoCompact-win-x64.exe > WslAutoCompact-win-x64.exe.sha256)

gh release create v0.1.0 \
  artifacts/release/WslAutoCompact-win-x64.exe \
  artifacts/release/WslAutoCompact-win-x64.exe.sha256 \
  --verify-tag \
  --title "WSL Auto Compact v0.1.0" \
  --generate-notes
```

## Development

The publish output embeds an application manifest with `requireAdministrator`, so the released `.exe` requests elevation automatically.

During development, running the app without elevation may detect distros but compact operations can fail.

## License

MIT
