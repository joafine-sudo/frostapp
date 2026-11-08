# Frost Client

An open-source, custom Minecraft launcher built with **WPF (.NET 8)**, developed by an independent company to distribute a customized version of the game ("Frost Client", based on Forge 1.8.9) ready to play out of the box — with account management, version/mod management, and Discord Rich Presence integration.

🌐 **Official website:** [eu.frostclient.org](https://eu.frostclient.org)

---

## ✨ Features

- **Account login**
  - Offline accounts (free player name, validated: 3–16 characters, letters/digits/`_`).
  - Microsoft accounts (OAuth via `CmlLib.Core.Auth.Microsoft`).
  - Multiple saved accounts, with fast switching and skin/avatar loading via `mc-heads.net` / `crafatar.com` / `minotar.net`, with automatic fallback if one provider is down.
- **Version management**
  - Pre-configured "Frost Client" version (Forge 1.8.9 + libraries + mods in the default `mods` folder).
  - Installation of other vanilla/Forge versions via `CmlLib.Core.Installer.Forge`.
  - Rename (display nickname) and delete installed versions.
  - Per-version mods folder (except "Frost Client", which uses the default `.minecraft` `mods` folder).
- **Automatic resource download**
  - On first use, downloads (or extracts, if bundled with the installer) the Frost Client version `.json`/`.jar` and all required libraries (including Forge).
  - Resource version marker (`.resources-version`) avoids reinstalling on every launch.
  - Progress screen with **Retry** / **Skip** options if a download fails.
- **Launch settings**
  - Maximum RAM (slider), custom Java path, extra JVM arguments, window resolution.
  - Optional auto-minimize of the launcher when the game starts.
- **Diagnostics**
  - Full log of the last launch at `%APPDATA%\FrostClient\last-launch.log` (version, Java used, resource status, RAM, game output, exit code).
  - Detailed on-screen error message on crash or resource preparation failure.
- **Discord Rich Presence** — shows on Discord whether the user is in the launcher or playing, and with which version.
- **First-run wizard** — guided setup of Java and login on first launch.
- **Localization (i18n)** — language auto-detected from the system, with all strings centralized in `Loc`.

---

## 🛠️ Tech stack

| Component | Purpose |
|---|---|
| .NET 8 / WPF | Graphical interface |
| `CmlLib.Core` | Downloading/installing vanilla Minecraft assets and launching the game process |
| `CmlLib.Core.Auth.Microsoft` | Microsoft login (Xbox Live / Minecraft) |
| `CmlLib.Core.Installer.Forge` | Installing "regular" Forge versions |
| `DiscordRichPresence` | Discord integration |
| Inno Setup | Windows installer (`.exe`) for the final distribution |

---

## 📁 Relevant folder structure
FrostClient/
├─ FrostClient.csproj
├─ MainWindow.xaml / MainWindow.xaml.cs
├─ default_version/
│ └─ frost client/
│ ├─ frost client.json # version manifest (libraries, main class, etc.)
│ └─ frost client.jar
├─ icon.ico, default.png, iceland.jpg, house.jpg, fox.jpg # assets embedded in the .exe
└─ installer/
└─ frostclient.iss # Inno Setup script

On the end user's PC, the launcher uses:
%APPDATA%.minecraft\ # standard Minecraft data folder
%APPDATA%.minecraft\versions\frost client
%APPDATA%.minecraft\libraries
%APPDATA%\FrostClient\settings.json # launcher settings
%APPDATA%\FrostClient\last-launch.log # diagnostic log


---

## 🚀 Build & publish (for development)

```powershell
dotnet restore
dotnet build -c Release

# single-file, self-contained publish for Windows x64
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

> ⚠️ **Important:** publishing produces the `.exe` at
> `bin\Release\net8.0-windows\win-x64\publish\`
> — but `CopyToPublishDirectory` copies the `default_version\` folder **next to** the `.exe`, not inside it.
> **Never distribute just the bare `.exe`**: either ship the entire `publish` folder, or package everything with the installer (next section).

---

## 📦 Building the installer (Inno Setup)

The installer (`installer/frostclient.iss`) bundles:
- the published launcher (`FrostClient.exe`);
- the Frost Client version `.json`/`.jar`, placed directly in `%APPDATA%\.minecraft\versions\frost client\`;
- all libraries (including Forge) in `%APPDATA%\.minecraft\libraries\`;
- a `.resources-version` file matching the `ResourcesVersion` constant in the code, so the launcher **doesn't try to download anything again** on first run.

To compile: open the `.iss` file in Inno Setup Compiler → `Build > Compile` (or `Ctrl+F9`). The final installer is output to `output\FrostClient-Setup.exe` (or whatever path is set in `OutputDir`).

---

## 🏷️ GitHub Releases

1. Commit and push the source code (excluding `bin/`, `obj/`, `publish/`, and large files — see `.gitignore` below).
2. Create the tag:
   ```powershell
   git tag minecraft
   git push origin minecraft
   ```
3. In **Releases → Draft a new release**, pick the tag, attach the `FrostClient-Setup.exe` produced by Inno Setup, and publish.
4. **Never commit** the published `.exe` or large resource `.zip` files directly into Git history — always use the Release attachment instead (or Git LFS, if versioning them is really necessary).

### Recommended `.gitignore`
```gitignore
bin/
obj/
**/publish/
*.zip
output/
```

---

## 🐞 Changelog — bug fixes

> Fill in / adjust this section with the specific bugs you fixed. Suggested structure below, already including the technical items identified during development:

### Download / resource bugs
- [x] **Forge 1.8.9 wasn't downloading on fresh PCs** — the library URL resolution fell back to a default repository (`libraries.minecraft.net`) that doesn't host the Forge jar when the entry in `frost client.json` didn't include an explicit `url`/`downloads.artifact.url`. Fixed by ensuring a full URL is present in the version manifest.
- [x] **Silent diagnostic failures** — generic HTTP error messages didn't indicate which file/URL had failed. The log (`last-launch.log`) and error dialog now report the actual cause (`_frostSetupInfo`).
- [x] **Incomplete distribution** — publishing as single-file doesn't embed the `default_version` folder into the `.exe`; fixed with an Inno Setup installer that bundles everything (launcher + version + libraries) and writes the resource marker (`.resources-version`) to skip the download step entirely.

### Text / localization bugs
- [ ] _(e.g.: untranslated strings, broken placeholders, truncated button text — describe each fix made here)_

### Interface bugs
- [ ] _(e.g.: previous account's avatar remaining visible when switching accounts — already handled in `UpdateProfileUI()`, which clears the image before fetching the new one; resources overlay not hiding correctly; etc.)_

### Repository / Git bugs
- [x] **Push rejected due to oversized file (>100MB)** — `FrostClient.exe` and `frost-resources.zip` were present in Git history. Resolved by cleaning the history (BFG/orphan branch) and adding a `.gitignore` for build artifacts and large packages.
- [x] **Tag/Release pointing to the wrong commit (only `index.html`)** — caused by a commit made in a folder without the actual source code. Fixed by recreating the tag after committing the full project from the correct folder.

---

## ❗ Known issues / limitations

- Downloading **vanilla** Minecraft files (client.jar, assets, lwjgl) is handled by `CmlLib.Core` on first launch of that version — this is **not** covered by the installer and still depends on the user's internet access / firewall rules.
- Antivirus/firewall software may block outgoing HTTPS connections from the launcher on some PCs — if Frost Client resources still fail to download even with the installer, check that network access is allowed.

---

## 🤝 Contributing

This is an open-source project maintained by an independent company. Contributions, bug reports, and pull requests are welcome — please open an issue describing the problem or proposed change before submitting a PR.

## 📄 License

Frost Client is **source-available**: the code is publicly visible so anyone can study, build, and modify it locally — but it is **not** released under a standard open-source license (MIT, GPL, etc.), since those licenses explicitly guarantee the right to resell, which this project does not grant.

**Legal Notice: Resale is Prohibited.** You may study, modify, and test the project locally, but you may **not** resell this project or any modified version of it. See the full [LICENSE](LICENSE.md) and the notice on the official website: [eu.frostclient.org/?page=modsoftware](https://eu.frostclient.org/?page=modsoftware).

This project, its source code, and its branding belong exclusively to **[eu.frostclient.org](https://eu.frostclient.org)**.

📜 See also: [Terms of Use](terms.md) — jurisdiction and regional legal notices.

## 🔗 Links

- Website: [https://eu.frostclient.org](https://eu.frostclient.org)

