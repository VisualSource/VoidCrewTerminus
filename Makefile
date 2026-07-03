PROJECT    := VoidCrewTerminus/VoidCrewTerminus.csproj
SCRIPTS    := $(abspath scripts)

.PHONY: build dev clean assets

# Copy the freshly exported Unity bundle into Assets/ under the mod-owned
# .terminus extension (the game auto-loads *.metem from the plugins dir; our
# AssetLoader owns *.terminus — see VoidCrewTerminus/AssetLoader.cs).
EXPORTED := VoidCrewUnityEditor/VoidCrewUnityEditor/Exported Assets
assets:
	cp "$(EXPORTED)/voidcrewterminus.metem" Assets/voidcrewterminus.metem_ext
	cp "$(EXPORTED)/voidcrewterminus.metem.manifest" Assets/voidcrewterminus.metem_ext.manifest

# Release build — prebuild generates MyPluginInfo.cs / manifest / README, postbuild zips output
build:
	PATH=$(SCRIPTS):$$PATH dotnet build $(PROJECT) -c Release

# Debug build — same prebuild (MyPluginInfo.cs updated), no zip since ZipOutput is checked at runtime
dev:
	PATH=$(SCRIPTS):$$PATH dotnet build $(PROJECT) -c Debug

clean:
	dotnet clean $(PROJECT)
