PROJECT := VoidCrewTerminus/VoidCrewTerminus.csproj
NOOP_DIR := /tmp/vct-noop-pwsh

.PHONY: build dev _noop-pwsh clean

# Release build — runs Thunderstore prebuild (generates MyPluginInfo.cs) and zips output
build:
	dotnet build $(PROJECT) -c Release

# Debug build — skips Thunderstore pre/postbuild by shimming pwsh with a no-op
dev: _noop-pwsh
	PATH=$(NOOP_DIR):$$PATH dotnet build $(PROJECT) -c Debug

_noop-pwsh:
	@mkdir -p $(NOOP_DIR)
	@printf '#!/bin/sh\n' > $(NOOP_DIR)/pwsh
	@chmod +x $(NOOP_DIR)/pwsh

clean:
	dotnet clean $(PROJECT)
