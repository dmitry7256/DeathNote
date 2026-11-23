#define AppName "Death Note"
#define AppVersion "3.00"
#define AppExe "DeathNote.exe"

; Per-user installation. Installed files are upgraded in-place by stable AppId.
; SQLite data and backups live under %LOCALAPPDATA%\DeathNote\data,
; outside {app}. The uninstaller NEVER removes the user's data directory.
[Setup]
AppId={{3DAD045A-E13F-4F6D-A8C9-071000E32100}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName=Death Note {#AppVersion}
AppPublisher=Death Note
VersionInfoVersion=3.0.0.0
DefaultDirName={localappdata}\Programs\Death Note
DefaultGroupName=Death Note
DisableProgramGroupPage=yes
DisableDirPage=yes
UsePreviousAppDir=yes
Uninstallable=yes
CreateUninstallRegKey=yes
UninstallDisplayName=Death Note
UninstallDisplayIcon={app}\{#AppExe}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=..
OutputBaseFilename=DeathNote_Setup_3.00_x64
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
WizardStyle=modern
SetupIconFile=..\DeathNote.Wpf\Assets\app.ico

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные ярлыки:"; Flags: unchecked

[Files]
Source: "..\published-installer\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.db,*.sqlite,*.sqlite3,*.db-wal,*.db-shm,*.sqlite-wal,*.sqlite-shm,*.pdb,*.log"

; All game integration assets are embedded in DeathNote.exe.
; The native Settings repair performs its own explicit confirmation and backups.

[Icons]
Name: "{group}\Death Note"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\Death Note"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

; If someone first installed via INSTALL_FROM_SOURCE.bat, remove its ARP entry.
; Data and existing backups are NEVER touched.
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\DeathNoteWpfSourceInstaller"; Flags: deletekey

[InstallDelete]
Type: files; Name: "{app}\UNINSTALL_FROM_SOURCE.ps1"
Type: files; Name: "{group}\Update Civ V Lua tracker.lnk"
Type: files; Name: "{app}\UPDATE_CIV5_LUA.bat"
Type: files; Name: "{app}\UPDATE_CIV5_LUA.ps1"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Запустить Death Note"; Flags: nowait postinstall skipifsilent runasoriginaluser
