```ini
#define MyAppName "Frost Client"
#define MyAppVersion "1.0.0"
#define MyAppExeName "FrostClient.exe"

#define ProjectDir "C:\Users\joaqu\Downloads\code_file\frost-source\FrostClient-Fixed-English\FrostClient"

#define LauncherPath ProjectDir + "\bin\Release\net8.0-windows\win-x64\publish\FrostClient.exe"
#define VersionDir ProjectDir + "\default_version\frost client"
#define ResourcesZip ProjectDir + "\frost-resources.zip"

[Setup]
AppId={{A7D9C4B2-61E8-4F35-9A20-7C8D3E5B1F60}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Frost Client

DefaultDirName={localappdata}\Programs\Frost Client
DefaultGroupName=Frost Client
DisableProgramGroupPage=yes
PrivilegesRequired=lowest

OutputDir=output
OutputBaseFilename=FrostClient-Setup

WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64

Compression=lzma2
SolidCompression=yes

UninstallDisplayIcon={app}\{#MyAppExeName}

CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: checkedonce

[Files]

; Install Frost Client launcher
Source: "{#LauncherPath}"; DestDir: "{app}"; Flags: ignoreversion

; Install resources ZIP next to launcher executable
Source: "{#ResourcesZip}"; DestDir: "{app}"; Flags: ignoreversion

; Install Minecraft version JAR
Source: "{#VersionDir}\frost client.jar"; DestDir: "{userappdata}\.minecraft\versions\frost client"; Flags: ignoreversion

; Install Minecraft version JSON
Source: "{#VersionDir}\frost client.json"; DestDir: "{userappdata}\.minecraft\versions\frost client"; Flags: ignoreversion

[Icons]

; Start Menu shortcut
Name: "{autoprograms}\Frost Client"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"

; Desktop shortcut
Name: "{autodesktop}\Frost Client"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]

; Launch Frost Client after installation
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Frost Client"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent
```