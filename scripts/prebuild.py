#!/usr/bin/env python3
"""
Bash-compatible replacement for VCMT.Thunderstore.DocBuilder.ps1.
Called by the pwsh shim with: OutputDir SolutionDir ProjectDir
"""
import sys
import os
import re
import json
import shutil


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


def update_csproj_element(content, tag, value):
    """Replace an XML element's text, inserting it if absent."""
    pattern = re.compile(rf"(<{re.escape(tag)}>)[^<]*(</{ re.escape(tag)}>)")
    new, n = pattern.subn(rf"\g<1>{value}\g<2>", content)
    if n == 0:
        new = content.replace(
            "</PropertyGroup>",
            f"    <{tag}>{value}</{tag}>\n  </PropertyGroup>",
            1,
        )
    return new


def main():
    if len(sys.argv) < 4:
        print("Usage: prebuild.py <OutputDir> <SolutionDir> <ProjectDir>")
        sys.exit(1)

    output_dir, solution_dir, project_dir = sys.argv[1], sys.argv[2], sys.argv[3]

    # OutDir from MSBuild can be relative — resolve to an absolute path via project_dir
    if not os.path.isabs(output_dir):
        output_dir = os.path.join(project_dir, output_dir)

    # SolutionDir is '*Undefined*' when building a .csproj directly (no .sln)
    if solution_dir == "*Undefined*" or not os.path.isdir(solution_dir):
        solution_dir = project_dir

    release_dir = os.path.join(project_dir, "ReleaseFiles")
    config_path = os.path.join(release_dir, "PluginInfo.config")
    readme_tmpl = os.path.join(release_dir, "README_Template.md")
    changelog_path = os.path.join(release_dir, "CHANGELOG.md")
    icon_path = os.path.join(release_dir, "icon.png")
    cs_info_path = os.path.join(project_dir, "MyPluginInfo.cs")

    os.makedirs(release_dir, exist_ok=True)

    if not os.path.exists(config_path):
        print(f"PreBuild: Error 2: {config_path} not found")
        sys.exit(2)

    config = parse_ini(config_path)
    props = config.get("ReleaseProperties", {})
    params = config.get("PrebuildExecParams", {})

    guid = props.get("GUID", "").strip()
    plugin_name = props.get("PluginName", "").strip()
    user_name = props.get("UserPluginName", "").strip() or plugin_name
    ts_name = props.get("ThunderstorePluginName", "").strip() or user_name.replace(" ", "_")
    version = props.get("PluginVersion", "").strip()
    game_version = props.get("GameVersion", "").strip()
    description = props.get("PluginDescription", "").strip() or plugin_name
    original_author = props.get("PluginOriginalAuthor", "").strip()
    authors = props.get("PluginAuthors", "").strip() or original_author
    webpage = props.get("WebpageLink", "").strip()
    ts_id = props.get("ThunderstoreID", "").strip()
    deps_str = props.get("DependencyStrings", "").strip()
    readme_out_setting = params.get("ProjectReadmeOutPath", "").strip()
    icon_error = params.get("IconError", "false").strip().lower() == "true"
    changelog_error = params.get("ChangelogError", "false").strip().lower() == "true"

    if len(description) > 250:
        print("PreBuild: Error 3: PluginDescription exceeds 250 characters")
        sys.exit(3)

    if not original_author and not guid:
        print("PreBuild: Error 4: PluginOriginalAuthor must be set when GUID is blank")
        sys.exit(4)

    # Resolve README output path
    if readme_out_setting == "SolutionDir":
        readme_out_path = os.path.join(solution_dir, "README.md")
    elif readme_out_setting == "ProjectDir":
        readme_out_path = os.path.join(project_dir, "README.md")
    elif readme_out_setting.startswith("ProjectDir"):
        readme_out_path = readme_out_setting.replace("ProjectDir", project_dir)
    elif readme_out_setting.startswith("SolutionDir"):
        readme_out_path = readme_out_setting.replace("SolutionDir", solution_dir)
    elif readme_out_setting:
        readme_out_path = readme_out_setting
    else:
        readme_out_path = None

    # Update .csproj
    print("Updating .csproj...")
    csproj_files = [f for f in os.listdir(project_dir) if f.endswith(".csproj")]
    namespace = "VoidCrewTerminus"
    if csproj_files:
        csproj_path = os.path.join(project_dir, csproj_files[0])
        with open(csproj_path, encoding="utf-8") as f:
            csproj = f.read()

        ns_m = re.search(r"<RootNamespace>([^<]+)</RootNamespace>", csproj)
        if ns_m:
            namespace = ns_m.group(1)

        # Get AssemblyName fallback when PluginName is blank
        if not plugin_name:
            an_m = re.search(r"<AssemblyName>([^<]+)</AssemblyName>", csproj)
            if an_m:
                plugin_name = an_m.group(1)
                user_name = user_name or plugin_name
                ts_name = ts_name or user_name.replace(" ", "_")
                description = description or plugin_name

        if version:
            csproj = update_csproj_element(csproj, "Version", version)
        if plugin_name:
            csproj = update_csproj_element(csproj, "AssemblyName", plugin_name)
        if description:
            csproj = update_csproj_element(csproj, "AssemblyTitle", description)

        with open(csproj_path, "w", encoding="utf-8") as f:
            f.write(csproj)

    # Generate MyPluginInfo.cs
    print("Generating MyPluginInfo.cs...")
    if guid:
        guid_line = f'        public const string PLUGIN_GUID = "{guid}";'
    else:
        guid_line = '        public const string PLUGIN_GUID = $"{PLUGIN_ORIGINAL_AUTHOR}.{PLUGIN_NAME}";'

    cs_content = (
        "#pragma warning disable CS1591\n"
        f"namespace {namespace}\n"
        "{\n"
        "    //Auto-Generated File. Created by PreBuild.ps1\n"
        "    public class MyPluginInfo\n"
        "    {\n"
        f"{guid_line}\n"
        f'        public const string PLUGIN_NAME = "{plugin_name}";\n'
        f'        public const string USERS_PLUGIN_NAME = "{user_name}";\n'
        f'        public const string PLUGIN_VERSION = "{version}";\n'
        f'        public const string PLUGIN_DESCRIPTION = "{description}";\n'
        f'        public const string PLUGIN_ORIGINAL_AUTHOR = "{original_author}";\n'
        f'        public const string PLUGIN_AUTHORS = "{authors}";\n'
        f'        public const string PLUGIN_THUNDERSTORE_ID = "{ts_id}";\n'
        "    }\n"
        "}\n"
        "#pragma warning restore CS1591\n"
    )
    with open(cs_info_path, "w", encoding="utf-8") as f:
        f.write(cs_content)

    # Write manifest.json
    print("Writing manifest.json...")
    os.makedirs(output_dir, exist_ok=True)
    deps = [d.strip() for d in deps_str.split(",") if d.strip()]
    manifest = {
        "name": ts_name,
        "version_number": version,
        "description": description,
        "website_url": webpage,
        "dependencies": deps,
    }
    with open(os.path.join(output_dir, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=4, ensure_ascii=False)

    # Process README
    if os.path.exists(readme_tmpl):
        print("Writing README.md...")
        with open(readme_tmpl, encoding="utf-8") as f:
            readme = f.read()
        readme = (
            readme.replace("[GameVersion]", game_version)
            .replace("[ModVersion]", version)
            .replace("[Authors]", authors)
            .replace("[Dependencies]", deps_str)
            .replace("[UserModName]", user_name)
            .replace("[ModName]", plugin_name)
            .replace("[Description]", description)
        )
        with open(os.path.join(output_dir, "README.md"), "w", encoding="utf-8") as f:
            f.write(readme)
        if readme_out_path:
            with open(readme_out_path, "w", encoding="utf-8") as f:
                f.write(readme)

    # Copy CHANGELOG
    if os.path.exists(changelog_path):
        print("Copying CHANGELOG.md...")
        with open(changelog_path, encoding="utf-8") as f:
            changelog_text = f.read()
        if f"## {version}" not in changelog_text:
            msg = f"PreBuild: {'Error 5' if changelog_error else 'Warning 5'}: CHANGELOG missing entry for {version}"
            print(msg)
            if changelog_error:
                sys.exit(5)
        shutil.copy2(changelog_path, os.path.join(output_dir, "CHANGELOG.md"))

    # Copy icon
    if os.path.exists(icon_path):
        print("Copying icon.png...")
        shutil.copy2(icon_path, os.path.join(output_dir, "icon.png"))
    else:
        msg = f"PreBuild: {'Error 6' if icon_error else 'Warning 6'}: icon.png not found in ReleaseFiles"
        print(msg)
        if icon_error:
            sys.exit(6)

    print("PreBuild Complete!")


if __name__ == "__main__":
    main()
