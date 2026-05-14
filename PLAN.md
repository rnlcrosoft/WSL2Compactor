# WSL2Compactor Improvement Plan

This plan defines what WSL2Compactor should handle, how it should handle it, and where the app should deliberately stop. The goal is not to add every possible automation path. The goal is to keep the tool predictable, recoverable, and useful for compacting WSL2 `ext4.vhdx` files.

## Scope Boundary

WSL2Compactor should automate the stable Windows-side compaction flow:

1. Detect WSL2 distributions from the current user's WSL registry metadata.
2. Select distributions that have an `ext4.vhdx`.
3. Run `fstrim` inside the selected distribution.
4. Shut WSL down.
5. Wait briefly for the VHDX file to become available.
6. Compact the VHDX through a known backend.
7. Report what happened and keep a detailed log.

WSL2Compactor should not become a general WSL repair tool, Hyper-V installer, disk recovery tool, or filesystem editor. Those paths involve machine-level state, reboot requirements, edition-specific behavior, or user data risk. Automating them would make the app less reliable.

When the app cannot safely continue, it should stop with a useful error and point the user to the issue tracker with the log file path. The app should prefer diagnosable failure over aggressive recovery.

## Non-Goals

- Do not enable Hyper-V automatically.
- Do not add `.mum` packages automatically.
- Do not schedule reboot continuation flows.
- Do not kill Docker Desktop, VS Code Remote, terminals, `wslhost.exe`, `vmmem`, or arbitrary user processes.
- Do not modify files inside the Linux filesystem directly.
- Do not run filesystem repair tools automatically.
- Do not compact arbitrary user-selected VHD/VHDX files.
- Do not format, initialize, mount, or assign drive letters to Linux partitions.
- Do not fake progress or ETA when Windows does not expose reliable progress.
- Do not treat low bytes saved as failure. A successful compact may save little if the VHDX was already compact.

## Backend Strategy

### Default Path

Use the VirtDisk API by default:

```text
fstrim -> wsl --shutdown -> CompactVirtualDisk(NO_ZERO_SCAN)
```

`fstrim` performs the Linux-side discard step. After that, `COMPACT_VIRTUAL_DISK_FLAG_NO_ZERO_SCAN` is the preferred default because a full zero scan can be very slow on large WSL2 disks and often adds little value for this workflow.

### Fallback Path

Use `diskpart compact vdisk` when the VirtDisk backend fails with an error that is likely backend-specific. The fallback should be explicit in the transcript and log:

```text
VirtDisk failed -> diskpart attach readonly -> compact vdisk -> detach vdisk
```

Do not fallback blindly when the failure suggests the VHDX is locked, missing, access-denied, or the user canceled the run. In those cases, a second backend is unlikely to help and may produce a more confusing error.

### Optional Path

Offer `Optimize-VHD` only when it is already installed. Do not try to install or enable Hyper-V from the app. If `Optimize-VHD` is selected, prefer the quick compaction mode that matches the `fstrim`-first workflow.

## Progress Model

Progress display should be truthful before it is pretty.

Use three display modes:

1. `PercentKnown`: Windows reports a useful monotonic percentage.
2. `PendingNoReliablePercent`: Windows reports the operation is pending, but the reported percentage is unavailable, stuck, or already 100%.
3. `Indeterminate`: The backend does not expose structured progress.

Rules:

- Treat `OperationStatus` as the completion signal for VirtDisk async operations.
- Never show `100%` as complete while `OperationStatus == ERROR_IO_PENDING`.
- Show ETA only in `PercentKnown` mode.
- In `PendingNoReliablePercent` mode, show elapsed time and the raw API state in the log, not a fake ETA.
- In `Indeterminate` mode, show elapsed time and the active backend/phase.
- Log raw `CurrentValue`, `CompletionValue`, calculated percentage, and operation status for VirtDisk diagnostics.

## Terminal UI Improvements

The terminal UI should remain interactive, but it should not feel like a dashboard pretending to know more than it does.

Implement or keep:

- Arrow-key selection for distros and backend.
- Default confirmation for normal execution: yes.
- Default confirmation for interruption: no.
- A compact pre-run table with distro name, VHDX path, size, backend, and selected state.
- A live operation area with active distro, backend, phase, elapsed time, and progress mode.
- A transcript area that preserves important events instead of overwriting them.
- A final summary table with start time, end time, elapsed time, before size, after size, bytes saved, backend, and result.
- A final screen that does not close on Enter.

Avoid:

- Repeating the same progress line every second in the transcript.
- Showing a progress bar when only elapsed time is reliable.
- Showing noisy warnings that do not change the user's decision.

## Logging Improvements

The log file should be the source for debugging a failed or suspicious run.

Each run should log:

- App version and command line.
- Windows version.
- Whether the process is elevated.
- Detected WSL distributions.
- Selected distributions.
- Selected backend.
- Confirmation answers.
- Each external command with start time, end time, exit code, stdout, and stderr.
- Before and after VHDX sizes.
- Backend transitions and fallback reasons.
- VirtDisk raw progress fields.
- Final result per distro.
- Log path.

The terminal should show a readable subset. The log file should keep the full details.

## Failure and Issue Reporting

Failures should end with enough context for a useful GitHub issue.

Implement a final failure panel that includes:

- Distro name.
- VHDX path.
- Backend.
- Phase.
- Error message.
- Exit code or Win32 error code when available.
- Log file path.
- GitHub issue URL.

Recommended issue URL:

```text
https://github.com/rnlcrosoft/WSL2Compactor/issues/new
```

Do not auto-open the browser during failure. Print the URL and the log path. Auto-opening external pages from an elevated process is unnecessary and can feel intrusive.

## Safety Behavior

The app should be conservative around user data and system state.

Implement:

- Admin check at startup with a clear message if elevation is missing.
- `wsl.exe` availability check.
- WSL2 distro detection check.
- VHDX existence check.
- Bounded VHDX lock retry after `wsl --shutdown`.
- Clear failure when the VHDX remains locked.
- A failure message that explains that another process may still be holding the VHDX.
- The log path and issue URL when a failure cannot be handled locally.
- Ctrl+C protection during compaction with default "keep running".
- Console close protection where Windows allows it.

Avoid:

- Force-closing processes that may hold the VHDX.
- Retrying forever.
- Re-attaching disks repeatedly after ambiguous failures.
- Hiding backend failures behind a generic error message.

Rationale:

Windows can expose that a VHDX is locked, but reliably identifying the correct owner process from a small elevated CLI app is not stable enough to automate. Killing broad candidates such as Docker Desktop, VS Code Remote, terminals, `wslhost.exe`, or `vmmem` risks data loss and surprising side effects. The app should run `wsl --shutdown`, wait for the lock to clear, and fail clearly if the file is still unavailable.

## Build and Release Profile

The release artifact should prioritize a small self-contained executable:

```text
PublishSingleFile=true
EnableCompressionInSingleFile=true
PublishTrimmed=true
PublishReadyToRun=false
self-contained win-x64
```

Rationale:

- Self-contained avoids requiring a separate .NET runtime install.
- Single-file keeps distribution simple.
- Single-file compression and trimming reduce release size.
- ReadyToRun is disabled because it increases file size and this app is not CPU-startup-sensitive.

Implementation:

- Keep these properties in the release workflow and both local publish scripts.
- Fail the release workflow if the final exe is missing.
- Add a size guard around the release exe. Start with a generous threshold such as 25 MiB so trimming regressions are caught without being fragile.

Add release checks:

- Build succeeds.
- Format verification succeeds.
- Publish succeeds.
- The release exe exists.
- The release exe size is below the documented threshold.
- SHA256 is generated for the release exe.

## Manual Test Matrix

The test plan should separate tests that can be run by one developer from tests that require specific machines.

### Local Repeatable Checks

Run on every release:

1. `dotnet build WSL2Compactor.slnx -c Release`.
2. `dotnet format WSL2Compactor.slnx --verify-no-changes`.
3. `./scripts/publish-win-x64.sh`.
4. Confirm the published exe exists.
5. Confirm the published exe is below the release size threshold.
6. Run the Japanese/old-name grep checks.
7. Launch the trimmed exe on Windows.

### Manual Windows Checks

Run when changing compaction, locking, progress, or elevation behavior:

1. Select one normal WSL2 distro and complete the default VirtDisk path.
2. Confirm the final summary shows before size, after size, saved bytes, backend, elapsed time, and log path.
3. Confirm low bytes saved is shown as success, not failure.
4. Keep a WSL terminal open, run compaction, and confirm `wsl --shutdown` is logged.
5. Simulate `fstrim` failure with a test distro or command override and confirm compaction continues.
6. Simulate VirtDisk failure with a backend test switch or injected failure and confirm DiskPart fallback only happens for fallback-safe errors.
7. Simulate a locked VHDX and confirm the app fails clearly with the log path and issue URL.
8. Press Ctrl+C during compaction and confirm the default action keeps the run alive.
9. Click the console close button during compaction and confirm the protection path behaves as documented.
10. Launch the trimmed release exe directly from Explorer.

### Environment Coverage Targets

These are useful, but not required for every release:

1. Windows 11 Pro with WSL2.
2. Windows 11 Home with WSL2.
3. At least one large VHDX.
4. A VHDX that saves very little space.
5. Optional `Optimize-VHD` path on a machine where it already exists.

If these environments are not available, do not pretend they were tested. Track them as unverified coverage in the release notes or issue tracker.

## Documentation Direction

The README should stay technical and small:

- What the app does.
- Why the steps exist.
- How to download.
- How to build.
- License.

Avoid marketing language, broad claims, and claims that require environment-specific proof.

## Practical Completion Criteria

The project is "good enough" when:

- The default VirtDisk path is fast for normal WSL2 `fstrim`-first compaction.
- Failures explain the backend, phase, command, and exit code.
- Unhandled failures print a log path and issue URL.
- Progress never claims completion before the backend completes.
- Logs are sufficient to debug user reports.
- Release artifacts are small and reproducible.
- The README does not overpromise.

Further automation beyond this point should be treated skeptically. Features that change Windows optional features, manage reboots, terminate user processes, repair filesystems, or handle arbitrary virtual disks are more likely to introduce data risk and unstable behavior than to improve this tool.
