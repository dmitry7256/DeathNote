# Removes only the script-installed application and its shortcuts/ARP entry.
# NEVER delete the private database, WAL, backups, or user settings.
$ErrorActionPreference='Stop'
$install=Join-Path $env:LOCALAPPDATA 'Programs\Death Note'
$registry='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeathNoteWpfSourceInstaller'
$running=@(Get-Process -Name 'DeathNote' -ErrorAction SilentlyContinue)
if($running.Count){throw 'Close Death Note before uninstalling.'}
$shortcut=Join-Path ([Environment]::GetFolderPath('Programs')) 'Death Note.lnk'
if(Test-Path $shortcut){Remove-Item -LiteralPath $shortcut -Force}
$gameShortcut=Join-Path ([Environment]::GetFolderPath('Programs')) 'Death Note - Update Civ V Lobby.lnk'
if(Test-Path $gameShortcut){Remove-Item -LiteralPath $gameShortcut -Force}
if(Test-Path $registry){Remove-Item -Path $registry -Recurse -Force}
if(Test-Path $install){Remove-Item -LiteralPath $install -Force -Recurse}
Write-Host 'Death Note binaries removed; user databases/backups were kept in LocalAppData\DeathNote\data.'
