PROJECT    := VoidCrewTerminus/VoidCrewTerminus.csproj
SCRIPTS    := $(abspath scripts)

.PHONY: build dev clean

# Release build — prebuild generates MyPluginInfo.cs / manifest / README, postbuild zips output
build:
	PATH=$(SCRIPTS):$$PATH dotnet build $(PROJECT) -c Release

# Debug build — same prebuild (MyPluginInfo.cs updated), no zip since ZipOutput is checked at runtime
dev:
	PATH=$(SCRIPTS):$$PATH dotnet build $(PROJECT) -c Debug

clean:
	dotnet clean $(PROJECT)
