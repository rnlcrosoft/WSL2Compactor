PROJECT := src/WSL2Compactor/WSL2Compactor.csproj
SOLUTION := WSL2Compactor.slnx
CONFIGURATION ?= Release
RUNTIME ?= win-x64
PUBLISH_DIR := .build/publish
PUBLISH_EXE := $(PUBLISH_DIR)/WSL2Compactor.exe
MAX_EXE_SIZE_BYTES ?= 26214400
PUBLISH_EXE_WIN ?= $(shell if command -v wslpath >/dev/null 2>&1; then wslpath -w "$(PUBLISH_EXE)"; elif command -v cygpath >/dev/null 2>&1; then cygpath -w "$(PUBLISH_EXE)"; else printf '%s' "$(PUBLISH_EXE)"; fi)
DESKTOP_EXE ?=

.PHONY: build publish format-check verify clean copy-desktop

build:
	dotnet build $(SOLUTION) -c $(CONFIGURATION)

publish:
	dotnet publish $(PROJECT) \
		-c $(CONFIGURATION) \
		-r $(RUNTIME) \
		--self-contained true \
		-p:PublishSingleFile=true \
		-p:EnableCompressionInSingleFile=true \
		-p:PublishTrimmed=true \
		-p:PublishReadyToRun=false \
		-o $(PUBLISH_DIR)
	@test -f "$(PUBLISH_EXE)" || (echo "Published executable not found: $(PUBLISH_EXE)" >&2; exit 1)
	@actual_size=$$(wc -c < "$(PUBLISH_EXE)"); \
	if [ "$$actual_size" -gt "$(MAX_EXE_SIZE_BYTES)" ]; then \
		echo "Published executable is too large: $$actual_size bytes. Limit: $(MAX_EXE_SIZE_BYTES) bytes." >&2; \
		exit 1; \
	fi; \
	echo "Published executable size: $$actual_size bytes"

format-check:
	dotnet format $(SOLUTION) --verify-no-changes

verify: build format-check publish

clean:
	rm -rf .build TestResults
	find src -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +

copy-desktop:
	@test -f "$(PUBLISH_EXE)" || (echo "Published executable not found: $(PUBLISH_EXE). Run make publish first." >&2; exit 1)
	@if [ -n "$(DESKTOP_EXE)" ]; then \
		target_dir=$$(dirname "$(DESKTOP_EXE)"); \
		test -d "$$target_dir" || (echo "Target directory not found: $$target_dir" >&2; exit 1); \
		cp "$(PUBLISH_EXE)" "$(DESKTOP_EXE)"; \
		echo "Copied to $(DESKTOP_EXE)"; \
	elif command -v powershell.exe >/dev/null 2>&1 && powershell.exe -NoProfile -Command "exit 0" >/dev/null 2>&1; then \
		powershell.exe -NoProfile -ExecutionPolicy Bypass -Command '$$ErrorActionPreference = "Stop"; $$source = "$(PUBLISH_EXE_WIN)"; $$desktop = [Environment]::GetFolderPath("Desktop"); if ([string]::IsNullOrWhiteSpace($$desktop)) { throw "Desktop folder could not be detected." }; $$target = Join-Path $$desktop "WSL2Compactor.exe"; Copy-Item -LiteralPath $$source -Destination $$target -Force; Write-Host "Copied to $$target"'; \
	else \
		target_dir=$$(find /mnt/c/Users -mindepth 2 -maxdepth 3 \( -path "*/Desktop" -o -path "*/OneDrive/Desktop" \) -type d -writable 2>/dev/null | grep -Ev "/(Default|Default User|Public|All Users)/" | head -n 1); \
		test -n "$$target_dir" || (echo "Desktop directory could not be detected. Pass DESKTOP_EXE=/path/to/WSL2Compactor.exe." >&2; exit 1); \
		target="$$target_dir/WSL2Compactor.exe"; \
		cp "$(PUBLISH_EXE)" "$$target"; \
		echo "Copied to $$target"; \
	fi
