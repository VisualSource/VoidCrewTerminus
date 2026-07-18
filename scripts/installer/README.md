# VoidCrewTerminus installer

A tiny helper so a tester can install/update the mod and grab logs **without
copying files by hand**. It downloads the mod from GitHub releases and drops it
into the right Thunderstore Mod Manager profile.

## For the tester — easy way (single .exe, nothing to install)

1. Get **`VoidCrewTerminusLocal.exe`** from the developer.
2. Double-click it (or run it from a terminal). Follow the prompts:
   - It finds your **Thunderstore Mod Manager** folder automatically (or asks for it).
   - Pick your **profile** (the one you launch the game with).
   - Choose **Install / update the mod** → it downloads the latest release and installs it.
3. Open Thunderstore Mod Manager, make sure that profile is selected and
   **VoidCrewTerminus** is enabled, then **Launch**.

> Windows may show a "Windows protected your PC" SmartScreen prompt for an
> unsigned exe — click **More info → Run anyway**.

## For the tester — from source (needs Bun)

1. **Install Bun** (one time): https://bun.sh — on Windows, in PowerShell run
   `powershell -c "irm bun.sh/install.ps1 | iex"`, then reopen the terminal.
2. Open a terminal **in this folder** and run:
   ```
   bun install     (first time only)
   bun start
   ```
3. Same prompts as above.

### Sending logs back

Run `bun start`, pick your profile, then **Collect the log file**. Choose
**Just VoidCrewTerminus log lines** (recommended — filters `LogOutput.log` to the
mod's lines only) or the full log. It's saved to your **Downloads** folder
(timestamped). Send that file to the developer. *(Launch the game at least once
first so there's a log to collect.)*

## What it does

- Downloads the release `.zip` from `github.com/VisualSource/VoidCrewTerminus`.
- Extracts it to `…/profiles/<profile>/BepInEx/plugins/VoidCrewTerminus/`
  (replacing the old copy; your config in `BepInEx/config` is left alone).
- Adds/updates the mod's entry in that profile's `mods.yml` (enabled).

## For the developer

The installer reads from **GitHub Releases**. For it to find anything, publish a
release whose **assets include the built mod zip** (`VoidCrewTerminus-x.y.z.zip`,
produced by `dotnet build -c Release`). Auto-generated "Source code" archives are
ignored — attach the real package. Version, description and dependencies shown in
`mods.yml` come from the `manifest.json` inside that zip.

Build the standalone Windows exe to hand to a tester:
```
bun install
bun run build:win     # → VoidCrewTerminusLocal.exe (bundles the Bun runtime)
```
Uses `Bun.YAML` for `mods.yml` (no `yaml` dependency); `fflate` for zip extraction.
