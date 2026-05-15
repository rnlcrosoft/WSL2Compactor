PROJECT := src/WSL2Compactor/WSL2Compactor.csproj
SOLUTION := WSL2Compactor.slnx
CONFIGURATION ?= Release
RUNTIME ?= win-x64
PUBLISH_DIR := .build/publish
PUBLISH_EXE := $(PUBLISH_DIR)/WSL2Compactor.exe
MAX_EXE_SIZE_BYTES ?= 26214400
DESKTOP_WIN_DIR ?= $(shell powershell.exe -NoProfile -Command "[Environment]::GetFolderPath('Desktop')" 2>/dev/null | tr -d '\r')
DESKTOP_DIR ?= $(shell win_path='$(DESKTOP_WIN_DIR)'; if command -v wslpath >/dev/null 2>&1; then wslpath -u "$$win_path"; elif command -v cygpath >/dev/null 2>&1; then cygpath -u "$$win_path"; else printf '%s' "$$win_path"; fi)
DESKTOP_EXE ?= $(DESKTOP_DIR)/WSL2Compactor.exe

.PHONY: build publish format-check verify clean copy-desktop run

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
	@test -d "$(DESKTOP_DIR)" || (echo "Desktop directory not found: $(DESKTOP_DIR)" >&2; exit 1)
	cp "$(PUBLISH_EXE)" "$(DESKTOP_EXE)"
	@echo "Copied to $(DESKTOP_EXE)"

run:
	$(MAKE) publish
	$(MAKE) copy-desktop
	powershell.exe -NoProfile -ExecutionPolicy Bypass -Command 'Start-Process -FilePath (Join-Path ([Environment]::GetFolderPath("Desktop")) "WSL2Compactor.exe") -Verb RunAs'
