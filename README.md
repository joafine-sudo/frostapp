# ❄️ Frost Client

Frost Client is a Windows desktop Minecraft launcher built with WPF and .NET 8.

> After a long time, the launcher we created has launched; we now have an update that displays the client icon on the player's nametag.

---

## What's New

- ❄️ **Client icon on nametags**: players using Frost Client now show the Frost snowflake icon next to their name (Minecraft 1.8.9 + Forge).
- 🚀 **Official launcher release**.

---

## Requirements

- Windows 10 or later
- .NET 8 SDK (Windows), only needed to build from source
- Java installed and available on `PATH` for running Minecraft and external Forge/Fabric installers
- A Microsoft account for Microsoft authentication; offline mode is also available

---

## Download

Grab the latest release from the **Releases** page of this repository, extract the whole `.zip`, and run `FrostClient.exe`.

> **Windows SmartScreen:** the executable is not code-signed, so Windows may show "Windows protected your PC". Click **More info → Run anyway**.

---

## Run from source

Open a terminal in the folder containing `FrostClient.csproj`, then run:

```powershell
dotnet restore
dotnet build -c Release
dotnet run
```

---

## Publish

To create a self-contained Windows x64 publish folder:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

The output is placed under `bin/Release/net8.0-windows/win-x64/publish/`. Distribute the entire publish folder, including the `mods` directory.

To publish a public release with the GitHub CLI (the repository must be public):

```powershell
Compress-Archive -Path .\bin\Release\net8.0-windows\win-x64\publish\* -DestinationPath .\FrostClient-1.1.0.zip -Force
gh release create v1.1.0 .\FrostClient-1.1.0.zip --title "Frost Client 1.1.0" --notes "Client icon on player nametags."
```

---

## Features

- Dark monochrome interface
- Offline player names and interactive Microsoft sign-in
- Installed Minecraft version discovery
- Vanilla version installation through CmlLib
- Forge and Fabric installer integration
- RAM setting and saved launcher preferences
- Portable `.jar` mod discovery and copy-to-game-folder behavior
- **Frost Client icon displayed on player nametags** (included mod, see below)

---

## Included mod: Client Icon Nametag

The `mods` folder ships with `clienticon-1.0.0.jar`, a client-side Forge mod that draws the Frost snowflake icon to the left of a player's nametag.

- **Minecraft version:** 1.8.9
- **Loader:** Forge `11.15.1.2318`
- **Side:** client only (nothing is needed on the server)
- **Visibility:** the icon is rendered locally. Other players only see it on nametags if they also run the mod. Your own nametag is shown in third person (F5).

### Changing the icon

Replace `src/main/resources/assets/clienticon/textures/icon.png` in the mod project with another square PNG (32x32 recommended, transparent background), then rebuild the mod. The size and spacing can be tuned with `ICON_SIZE` and `ICON_GAP` in `CustomRenderPlayer.java`.

### Building the mod

The mod uses ForgeGradle 2.1 and requires a **JDK 8** (not just a JRE) and Gradle 2.14.

```powershell
$env:JAVA_HOME = "C:\Program Files\Eclipse Adoptium\jdk-8.0.504.1-hotspot"
.\build.bat
```

The jar is created in `build\libs\clienticon-1.0.0.jar`. Do not use the `-sources.jar`.

---

## Notes and limitations

- Forge installer versions are explicitly configured for a small set of Minecraft releases. Unsupported versions show an error instead of constructing a likely-invalid download URL.
- Java must be installed separately. A self-contained .NET publish does not include Java or Minecraft.
- Forge/Fabric installation depends on the external installers and network availability.
- The client icon mod only works on **Minecraft 1.8.9 with Forge**. It will not load on other versions or on Fabric.
- Only install mods from sources you trust, and use mods compatible with your selected Minecraft version and loader.
- Build and test on Windows with the .NET 8 SDK.

---

## Troubleshooting

| Problem | Fix |
| --- | --- |
| Icon doesn't show | Make sure you launched the **Forge 1.8.9** profile and that `clienticon-1.0.0.jar` is in the game's `mods` folder. In singleplayer, press **F5** to see your own nametag. |
| Game crashes on start | Check that only one copy of the mod is in `mods`, and that you are on Forge 1.8.9. |
| `Could not find tools.jar` when building the mod | You have a JRE installed. Install a **JDK 8** and set `JAVA_HOME`. |
| SmartScreen warning when opening the launcher | Click **More info → Run anyway** (the executable is unsigned). |

---

## License

Add your license here.
