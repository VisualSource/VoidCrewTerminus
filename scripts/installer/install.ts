#!/usr/bin/env bun
/**
 * VoidCrewTerminus installer / log collector.
 *
 * For a non-technical tester: run it, pick your Thunderstore Mod Manager profile,
 * and it will download the latest mod release from GitHub and drop it into the
 * right folder — no manual file copying. It can also grab the log file for you
 * to send back.
 *
 * Usage (once Bun is installed — https://bun.sh):
 *     bun install        (first time only, from this folder)
 *     bun start
 */

import {
  intro,
  outro,
  select,
  text,
  confirm,
  spinner,
  isCancel,
  cancel,
  note,
  log,
} from "@clack/prompts";
import { unzipSync } from "fflate";
import { homedir } from "node:os";
import { join, dirname, basename } from "node:path";
import {
  existsSync,
  readdirSync,
  statSync,
  mkdirSync,
  rmSync,
  readFileSync,
  writeFileSync,
  copyFileSync,
} from "node:fs";

// ---- mod identity (stable) --------------------------------------------------

const REPO = "VisualSource/VoidCrewTerminus"; // GitHub owner/repo
const MOD_NAME = "VoidCrewTerminus"; // plugins/<this> folder + mods.yml displayName
const AUTHOR = "VisualSource";
// Thunderstore package identity "<Author>-<Package>". It MUST be present in
// mods.yml as `name` (missing it is what makes TMM throw "Path undefined").
const PACKAGE_NAME = `${AUTHOR}-${MOD_NAME}`;
const WEBSITE = `https://github.com/${REPO}`;
// Substring that tags this mod's lines in BepInEx/LogOutput.log, e.g.
// "[Info   :VoidCrewTerminus] …". Used to extract just the mod's log lines.
const MOD_LOG_TAG = "VoidCrew";
// Fallback if a release package omits a manifest.json.
const FALLBACK_DEPENDENCIES = [
  "BepInEx-BepInExPack-5.4.2100",
  "NihilityShift-VoidManager-1.2.10",
];

// Thunderstore Mod Manager lays its data out under %APPDATA% on Windows.
// DataFolder/<game>/profiles/<profile>/{mods.yml, BepInEx/plugins/...}
const TMM_RELATIVE = join("Thunderstore Mod Manager", "DataFolder", "VoidCrew");

// ---- small helpers ----------------------------------------------------------

/** Thrown by bail() so the entry point can still pause before the window closes. */
class BailError extends Error {}

function bail(message: string): never {
  cancel(message);
  throw new BailError(message);
}

/**
 * Keep the console window open until the user acknowledges — important for
 * testers who double-click the .exe, where the window would otherwise vanish
 * (taking any success/error message with it) the instant we exit.
 */
async function pause(): Promise<void> {
  await text({ message: "Press Enter to close this window…", placeholder: "" });
}

/** Abort cleanly if the user hit Ctrl+C / Esc at a prompt. */
function guard<T>(value: T | symbol): T {
  if (isCancel(value)) bail("Cancelled — nothing was changed.");
  return value as T;
}

function listDirs(path: string): string[] {
  if (!existsSync(path)) return [];
  return readdirSync(path).filter((name) => {
    try {
      return statSync(join(path, name)).isDirectory();
    } catch {
      return false;
    }
  });
}

function timestamp(): string {
  return new Date().toISOString().replace(/[:.]/g, "-").replace("T", "_").slice(0, 19);
}

// ---- locate the mod manager data folder + profile ---------------------------

async function resolveDataFolder(): Promise<string> {
  const appData = process.env.APPDATA; // Windows roaming
  const candidates = [
    appData ? join(appData, TMM_RELATIVE) : null,
    // Best-effort for the rare non-Windows tester.
    join(homedir(), ".config", TMM_RELATIVE),
  ].filter(Boolean) as string[];

  const found = candidates.find((p) => existsSync(join(p, "profiles")));
  if (found) {
    log.success(`Found Thunderstore Mod Manager data at:\n  ${found}`);
    return found;
  }

  log.warn(
    "Couldn't auto-find the Thunderstore Mod Manager folder.\n" +
    "It's usually at %APPDATA%\\Thunderstore Mod Manager\\DataFolder\\VoidCrew",
  );
  const entered = guard(
    await text({
      message: "Paste the full path to the VoidCrew data folder (the one containing 'profiles'):",
      placeholder: candidates[0] ?? "",
      validate(v) {
        if (!v) return "Path is required.";
        if (!existsSync(join(v, "profiles")))
          return "That folder has no 'profiles' subfolder — check the path.";
      },
    }),
  );
  return entered.trim();
}

async function selectProfile(dataFolder: string): Promise<string> {
  const profilesDir = join(dataFolder, "profiles");
  const profiles = listDirs(profilesDir);
  if (profiles.length === 0)
    bail(`No profiles found under ${profilesDir}. Create/select a profile in the mod manager first.`);

  let chosen: string;
  if (profiles.length === 1) {
    chosen = profiles[0]!
    log.info(`Using the only profile: ${chosen}`);
  } else {
    chosen = guard(
      await select({
        message: "Which mod manager profile?",
        options: profiles.map((p) => ({ value: p, label: p })),
      }),
    ) as string;
  }

  const profileDir = join(profilesDir, chosen!);
  if (!existsSync(join(profileDir, "BepInEx"))) {
    const proceed = guard(
      await confirm({
        message: `Profile "${chosen!}" has no BepInEx folder yet. Continue anyway (it will be created)?`,
        initialValue: false,
      }),
    );
    if (!proceed) bail("Pick a profile that already has BepInEx installed, then re-run.");
  }
  return profileDir;
}

// ---- GitHub releases --------------------------------------------------------

interface ReleaseAsset {
  name: string;
  browser_download_url: string;
  size: number;
}
interface Release {
  tag_name: string;
  name: string | null;
  prerelease: boolean;
  draft: boolean;
  assets: ReleaseAsset[];
}

async function githubJson<T>(url: string): Promise<T> {
  const res = await fetch(url, {
    headers: {
      "User-Agent": `${MOD_NAME}-installer`,
      Accept: "application/vnd.github+json",
    },
  });
  if (!res.ok) {
    const hint =
      res.status === 403
        ? " (GitHub rate limit — wait a bit and retry)"
        : res.status === 404
          ? " (repo or releases not found)"
          : "";
    throw new Error(`GitHub API ${res.status}${hint}`);
  }
  return (await res.json()) as T;
}

async function pickRelease(): Promise<{ release: Release; asset: ReleaseAsset }> {
  const s = spinner();
  s.start("Fetching releases from GitHub…");
  let releases: Release[];
  try {
    releases = (await githubJson<Release[]>(`https://api.github.com/repos/${REPO}/releases`))
      .filter((r) => !r.draft && r.assets.some((a) => a.name.toLowerCase().endsWith(".zip")));
  } catch (e) {
    s.stop("Failed to reach GitHub.");
    bail(String(e instanceof Error ? e.message : e));
  }
  s.stop(`Found ${releases.length} release${releases.length === 1 ? "" : "s"}.`);

  if (releases.length === 0)
    bail(`No releases with a .zip asset on ${REPO}. Ask the developer to publish one.`);

  const choice = guard(
    await select({
      message: "Which version to install?",
      options: releases.slice(0, 15).map((r, i) => ({
        value: i,
        label: r.name || r.tag_name,
        hint: i === 0 ? "latest" : r.prerelease ? "pre-release" : undefined,
      })),
      initialValue: 0,
    }),
  ) as number;

  const release = releases[choice];

  if (!release) bail("Failed to selected release!");

  // Prefer an asset that looks like the mod package; else the first .zip.
  const asset =
    release.assets.find((a) => a.name.toLowerCase().includes(MOD_NAME.toLowerCase()) && a.name.endsWith(".zip")) ??
    release.assets.find((a) => a.name.toLowerCase().endsWith(".zip"))!;
  return { release, asset };
}

// ---- install ----------------------------------------------------------------

interface PackageManifest {
  name?: string;
  version_number?: string;
  description?: string;
  website_url?: string;
  dependencies?: string[];
}

function parseVersion(v: string): { major: number; minor: number; patch: number } {
  const [major = 0, minor = 0, patch = 0] = v
    .replace(/^v/i, "")
    .split(".")
    .map((n) => parseInt(n, 10) || 0);
  return { major, minor, patch };
}

async function install(profileDir: string) {
  const { release, asset } = await pickRelease();

  const s = spinner();
  s.start(`Downloading ${asset.name} (${(asset.size / 1024).toFixed(0)} KB)…`);
  let bytes: Uint8Array;
  try {
    const res = await fetch(asset.browser_download_url, {
      headers: { "User-Agent": `${MOD_NAME}-installer` },
    });
    if (!res.ok) throw new Error(`download failed (HTTP ${res.status})`);
    bytes = new Uint8Array(await res.arrayBuffer());
  } catch (e) {
    s.stop("Download failed.");
    bail(String(e instanceof Error ? e.message : e));
  }
  s.stop(`Downloaded ${asset.name}.`);

  // Extract into plugins/<MOD_NAME>, replacing any previous copy but leaving the
  // config folder (BepInEx/config) and everything else untouched.
  const pluginsDir = join(profileDir, "BepInEx", "plugins");
  const modDir = join(pluginsDir, MOD_NAME);

  let files: Record<string, Uint8Array>;
  try {
    files = unzipSync(bytes);
  } catch (e) {
    bail(`The downloaded archive couldn't be read as a zip: ${e}`);
  }

  if (existsSync(modDir)) rmSync(modDir, { recursive: true, force: true });
  mkdirSync(modDir, { recursive: true });

  let written = 0;
  let manifest: PackageManifest = {};
  for (const [name, data] of Object.entries(files)) {
    if (name.endsWith("/") || data.length === 0) continue; // directory entry
    const outPath = join(modDir, name);
    mkdirSync(dirname(outPath), { recursive: true });
    writeFileSync(outPath, data);
    written++;
    if (basename(name).toLowerCase() === "manifest.json") {
      try {
        manifest = JSON.parse(new TextDecoder().decode(data));
      } catch {
        /* ignore a malformed manifest — we have fallbacks */
      }
    }
  }
  log.success(`Extracted ${written} file(s) to:\n  ${modDir}`);

  // Update mods.yml so the manager shows it installed + enabled.
  const version = manifest.version_number ?? release.tag_name.replace(/^v/i, "");
  updateModsYml(profileDir, {
    version,
    description: manifest.description ?? "",
    website: manifest.website_url || WEBSITE,
    dependencies: manifest.dependencies?.length ? manifest.dependencies : FALLBACK_DEPENDENCIES,
  });

  note(
    `Installed ${MOD_NAME} v${version} into profile:\n  ${basename(profileDir)}\n\n` +
    `Next: open Thunderstore Mod Manager, make sure this profile is selected,\n` +
    `confirm ${MOD_NAME} shows as enabled, then Launch the game.`,
    "All set",
  );
}

// ---- mods.yml ---------------------------------------------------------------

interface ModEntry {
  manifestVersion: number;
  name: string;
  authorName: string;
  websiteUrl: string;
  displayName: string;
  description: string | null;
  gameVersion: string;
  networkMode: string;
  packageType: string;
  installMode: string;
  installedAtTime: number;
  loaders: unknown[];
  dependencies: string[];
  incompatibilities: unknown[];
  optionalDependencies: unknown[];
  versionNumber: { major: number; minor: number; patch: number };
  enabled: boolean;
  onlineSource: boolean;
}

function updateModsYml(
  profileDir: string,
  info: { version: string; description: string; website: string; dependencies: string[] },
) {
  const modsYml = join(profileDir, "mods.yml");

  let list: ModEntry[] = [];
  if (existsSync(modsYml)) {
    try {
      const parsed = Bun.YAML.parse(readFileSync(modsYml, "utf8"));
      if (Array.isArray(parsed)) list = parsed as ModEntry[];
    } catch (e) {
      log.warn(`mods.yml couldn't be parsed and will be rewritten fresh: ${e}`);
    }
  }

  const idx = list.findIndex((e) => e?.name === PACKAGE_NAME || e?.displayName === MOD_NAME);
  const existing = idx >= 0 ? list[idx] : null;

  const entry: ModEntry = {
    manifestVersion: 1,
    name: PACKAGE_NAME,
    authorName: AUTHOR,
    websiteUrl: info.website,
    displayName: MOD_NAME,
    description: info.description || null,
    gameVersion: "0",
    networkMode: "both",
    packageType: "other",
    installMode: "managed",
    // Preserve the original install time on update; stamp now on first install.
    installedAtTime: existing?.installedAtTime ?? Date.now(),
    loaders: [],
    dependencies: info.dependencies,
    incompatibilities: [],
    optionalDependencies: [],
    versionNumber: parseVersion(info.version),
    enabled: existing?.enabled ?? true,
    onlineSource: false,
  };

  if (idx >= 0) list[idx] = entry;
  else list.push(entry);

  // Bun's YAML: (value, replacer, space) like JSON.stringify — indent 2 gives
  // the block style the mod manager writes.
  writeFileSync(modsYml, Bun.YAML.stringify(list, undefined, 2));
  log.success(`${idx >= 0 ? "Updated" : "Added"} the ${MOD_NAME} entry in mods.yml.`);
}

// ---- log collection ---------------------------------------------------------

async function collectLogs(profileDir: string) {
  const logPath = join(profileDir, "BepInEx", "LogOutput.log");

  if (!existsSync(logPath)) {
    bail(
      `No log file found at:\n  ${logPath}\n` +
      `Launch the game once through the mod manager first, then re-run this.`,
    );
  }

  const scope = guard(
    await select({
      message: "What to save?",
      options: [
        { value: "mod", label: `Just ${MOD_NAME} log lines`, hint: `lines containing "${MOD_LOG_TAG}" — recommended` },
        { value: "full", label: "The full log file", hint: "everything, larger" },
      ],
      initialValue: "mod",
    }),
  )

  const downloads = join(homedir(), "Downloads");
  const destDir = existsSync(downloads) ? downloads : homedir();

  if (scope === "full") {
    const dest = join(destDir, `${MOD_NAME}-LogOutput-${timestamp()}.log`);
    copyFileSync(logPath, dest);
    const kb = (statSync(dest).size / 1024).toFixed(0);
    note(`Saved the full log (${kb} KB) to:\n  ${dest}\n\nSend that file to the developer.`, "Log saved");
    return;
  }

  // Filter to just this mod's lines — the JS equivalent of
  //   Select-String -Path LogOutput.log -Pattern "VoidCrew"
  const text = readFileSync(logPath, "utf8");
  const matched = text.split(/\r?\n/).filter((line) => line.includes(MOD_LOG_TAG));

  if (matched.length === 0) {
    bail(
      `No lines containing "${MOD_LOG_TAG}" in the log yet.\n` +
      `Launch the game with the mod enabled, reproduce the issue, then re-run this.`,
    );
  }

  const dest = join(destDir, `${MOD_NAME}-log-${timestamp()}.log`);
  writeFileSync(dest, matched.join("\n") + "\n");
  const kb = (statSync(dest).size / 1024).toFixed(1);
  note(
    `Saved ${matched.length} ${MOD_NAME} log line(s) (${kb} KB) to:\n  ${dest}\n\nSend that file to the developer.`,
    "Log saved",
  );
}

// ---- main -------------------------------------------------------------------

async function main() {
  intro(`${MOD_NAME} installer`);

  const dataFolder = await resolveDataFolder();
  const profileDir = await selectProfile(dataFolder);

  const action = guard(
    await select({
      message: "What do you want to do?",
      options: [
        { value: "install", label: "Install / update the mod", hint: "download latest from GitHub" },
        { value: "logs", label: "Collect the log file", hint: "copy LogOutput.log to Downloads" },
      ],
    }),
  ) as "install" | "logs";

  if (action === "install") await install(profileDir);
  else await collectLogs(profileDir);

  outro("Done.");
}

let failed = false;
main()
  .catch((e) => {
    failed = true;
    // BailError has already been shown to the user via cancel(); only surface
    // genuinely unexpected failures here.
    if (!(e instanceof BailError)) {
      cancel(`Unexpected error: ${e instanceof Error ? e.message : e}`);
    }
  })
  .finally(async () => {
    await pause();
    process.exit(failed ? 1 : 0);
  });
