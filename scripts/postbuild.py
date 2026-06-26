#!/usr/bin/env python3
"""
Bash-compatible replacement for VCMT.Thunderstore.ZipBuilder.ps1.
Called by the pwsh shim with: OutputDir SolutionDir ProjectDir
"""
import sys
import os
import re
import zipfile


def parse_ini(path):
    config = {}
    section = None
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith(";"):
                continue
            m = re.match(r"^\[(.+)\]$", line)
            if m:
                section = m.group(1)
                config[section] = {}
                continue
            if section and "=" in line:
                k, _, v = line.partition("=")
                config[section][k.strip()] = v.strip()
    return config


def main():
    if len(sys.argv) < 4:
        print("Usage: postbuild.py <OutputDir> <SolutionDir> <ProjectDir>")
        sys.exit(1)

    output_dir, solution_dir, project_dir = sys.argv[1], sys.argv[2], sys.argv[3]

    # OutDir from MSBuild can be relative
    if not os.path.isabs(output_dir):
        output_dir = os.path.join(project_dir, output_dir)

    config_path = os.path.join(project_dir, "ReleaseFiles", "PluginInfo.config")
    icon_path = os.path.join(project_dir, "ReleaseFiles", "icon.png")

    config = parse_ini(config_path)
    props = config.get("ReleaseProperties", {})
    params = config.get("PrebuildExecParams", {})

    plugin_name = props.get("PluginName", "").strip()
    version = props.get("PluginVersion", "").strip()
    build_zip = params.get("ZipOutput", "false").strip().lower() == "true"
    icon_error = params.get("IconError", "false").strip().lower() == "true"

    # Fallback: read AssemblyName from csproj if PluginName is blank
    if not plugin_name:
        csproj_files = [f for f in os.listdir(project_dir) if f.endswith(".csproj")]
        if csproj_files:
            with open(os.path.join(project_dir, csproj_files[0]), encoding="utf-8") as f:
                content = f.read()
            m = re.search(r"<AssemblyName>([^<]+)</AssemblyName>", content)
            if m:
                plugin_name = m.group(1)

    print("Starting Postbuild...")

    if build_zip and version:
        print("Building zip...")
        releases_dir = os.path.join(output_dir, "Releases")
        os.makedirs(releases_dir, exist_ok=True)

        zip_path = os.path.join(releases_dir, f"{plugin_name}-{version}.zip")

        candidates = [
            os.path.join(output_dir, "README.md"),
            os.path.join(output_dir, "manifest.json"),
            os.path.join(output_dir, f"{plugin_name}.dll"),
            os.path.join(output_dir, "CHANGELOG.md"),
        ]

        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
            for path in candidates:
                if os.path.exists(path):
                    zf.write(path, os.path.basename(path))

            icon_out = os.path.join(output_dir, "icon.png")
            if os.path.exists(icon_out):
                zf.write(icon_out, "icon.png")
            else:
                msg = f"PostBuild: {'Error 6' if icon_error else 'Warning 6'}: icon.png not found"
                print(msg)
                if icon_error:
                    sys.exit(6)

        print(f"Zip: {zip_path}")

    print("PostBuild Complete!")


if __name__ == "__main__":
    main()
