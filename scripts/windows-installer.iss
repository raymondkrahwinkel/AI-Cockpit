; Inno Setup script for the AI-Cockpit Windows installer.
;
; Why this exists: the release already ships a portable single-file Cockpit.App.exe — download and double-click.
; The installer is the other half of that: it puts the app in Program Files, gives it a Start-menu and desktop
; entry, and registers an uninstaller in "Apps & features", so it looks and updates like an installed program
; rather than a loose file the operator has to keep somewhere.
;
; Driven entirely from the command line so the one source of truth for the version is MSBuild, never this file
; (the same rule the workflows follow). scripts/package-windows-installer.ps1 passes:
;   /DSourceExe=<path to the published Cockpit.App.exe>
;   /DAppVersion=<full display version, e.g. 0.3.0-nightly.42>
;   /DAppVersionNumeric=<x.y.z from VersionPrefix, for the file's VersionInfo>
;   /DOutputDir=<folder for the Setup.exe>
;   /DOutputBase=<Setup.exe filename without extension>
;
; Per-user by design (PrivilegesRequired=lowest): this is a single-operator, self-hosted app, and a per-user
; install needs no UAC prompt and no admin rights. {autopf} then resolves to the per-user programs folder.

#ifndef SourceExe
  #error SourceExe must be passed with /DSourceExe=...
#endif
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.0.0"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\windows"
#endif
#ifndef OutputBase
  #define OutputBase "AI-Cockpit-Setup"
#endif

[Setup]
; A stable AppId so a later version replaces this one in place rather than installing beside it. Never change it.
AppId={{9C2F5B7E-3D4A-4E1C-9B6F-1A2C3D4E5F60}
AppName=AI-Cockpit
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersionNumeric}
AppPublisher=Raymond Krahwinkel
DefaultDirName={autopf}\AI-Cockpit
DefaultGroupName=AI-Cockpit
; Show the license acceptance page in the wizard. Path is relative to this .iss (scripts/), so it points at the
; repo-root LICENSE — the same Commons Clause + Apache 2.0 the app ships under. Inno detects RTF vs plain text
; from the file's header, so the extensionless LICENSE loads correctly as plain text.
LicenseFile=..\LICENSE
UninstallDisplayName=AI-Cockpit
UninstallDisplayIcon={app}\Cockpit.App.exe
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBase}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Close a running cockpit (via the Windows Restart Manager) before overwriting its exe — a running program locks
; its own file, and without this the install would fail on "file in use". RestartApplications=no: the operator
; relaunches it themselves, or ticks the "run now" box on the last page.
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "Cockpit.App.exe"; Flags: ignoreversion

[Icons]
Name: "{group}\AI-Cockpit"; Filename: "{app}\Cockpit.App.exe"
Name: "{group}\Uninstall AI-Cockpit"; Filename: "{uninstallexe}"
Name: "{userdesktop}\AI-Cockpit"; Filename: "{app}\Cockpit.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Cockpit.App.exe"; Description: "Launch AI-Cockpit"; Flags: nowait postinstall skipifsilent
