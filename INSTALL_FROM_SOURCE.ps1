# One-click per-user installation directly from source when Inno Setup is not installed.
# This is a Windows installation script, not the distributable Setup.exe.
# Installs to LocalAppData\Programs, registers in Windows Installed Apps,
# and updates binaries without touching the user SQLite database.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
# Wait until Death Note exits rather than failing the installation. Never force-kill
# the process: SQLite state and unsaved changes must remain safe.
function Wait-ForDeathNoteExit {
  $running=@(Get-Process -Name 'DeathNote' -ErrorAction SilentlyContinue)
  if($running.Count -gt 0){
    Write-Host 'Death Note is still running (possibly in the system tray).' -ForegroundColor Yellow
    Write-Host 'Please close Death Note from the app or tray. Installation will resume automatically.' -ForegroundColor Yellow
    Write-Host 'If it stays open, use Task Manager > Details > DeathNote.exe > End task. Press Ctrl+C to cancel.' -ForegroundColor Yellow
    while(@(Get-Process -Name 'DeathNote' -ErrorAction SilentlyContinue).Count -gt 0){
      Start-Sleep -Seconds 2
    }
    Write-Host 'Death Note has exited. Continuing installation...' -ForegroundColor Green
  }
}
Wait-ForDeathNoteExit
& (Join-Path $root 'PUBLISH_INSTALLER.ps1')
$payload=Join-Path $root 'published-installer'
$parent=Join-Path $env:LOCALAPPDATA 'Programs'
$target=Join-Path $parent 'Death Note'
$registry='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeathNoteWpfSourceInstaller'
$inno='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{3DAD045A-E13F-4F6D-A8C9-071000E32100}_is1'
# Never overwrite an Inno-managed installation outside its own updater.
if(Test-Path $inno){throw 'An Inno Setup installation already exists. Upgrade it using DeathNote_Setup_*.exe instead.'}
Wait-ForDeathNoteExit
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$tag=[Guid]::NewGuid().ToString('N')
$staged=Join-Path $parent ('Death Note.new-'+$tag)
$backup=Join-Path $parent ('Death Note.previous-'+$tag)
$swapped=$false
$completed=$false
try {
  New-Item -ItemType Directory -Path $staged -Force | Out-Null
  Copy-Item -Path (Join-Path $payload '*') -Destination $staged -Recurse -Force
  if(-not (Test-Path (Join-Path $staged 'DeathNote.exe'))){throw 'Missing executable in staging folder.'}
  Copy-Item -Path (Join-Path $root 'UNINSTALL_FROM_SOURCE.ps1') -Destination (Join-Path $staged 'UNINSTALL_FROM_SOURCE.ps1') -Force
  # Complete Civ V inspection and repair is embedded in DeathNote.exe (Settings).
  # Never install old Python or separate Lua patch scripts as runtime dependencies.
  if(Test-Path $target){Move-Item -LiteralPath $target -Destination $backup -ErrorAction Stop}
  try {
    Move-Item -LiteralPath $staged -Destination $target -ErrorAction Stop
    $swapped=$true
  } catch {
    if(Test-Path $backup){Move-Item -LiteralPath $backup -Destination $target -ErrorAction SilentlyContinue}
    throw
  }
  $shell=New-Object -ComObject WScript.Shell
  $shortcut=Join-Path ([Environment]::GetFolderPath('Programs')) 'Death Note.lnk'
  $link=$shell.CreateShortcut($shortcut)
  $link.TargetPath=Join-Path $target 'DeathNote.exe'
  $link.WorkingDirectory=$target
  $link.IconLocation=Join-Path $target 'DeathNote.exe'
  $link.Save()
  # Clean the obsolete shortcut from older installations; game repair is now in Settings.
  $oldShortcut=Join-Path ([Environment]::GetFolderPath('Programs')) 'Death Note - Update Civ V Lobby.lnk'
  if(Test-Path $oldShortcut){Remove-Item -LiteralPath $oldShortcut -Force -ErrorAction SilentlyContinue}
  New-Item -Path $registry -Force | Out-Null
  $uninstall='powershell.exe -NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $target 'UNINSTALL_FROM_SOURCE.ps1')+'"'
  New-ItemProperty -Path $registry -Name DisplayName -Value 'Death Note' -PropertyType String -Force | Out-Null
  New-ItemProperty -Path $registry -Name DisplayVersion -Value '3.01' -PropertyType String -Force | Out-Null
  New-ItemProperty -Path $registry -Name Publisher -Value 'Death Note' -PropertyType String -Force | Out-Null
  New-ItemProperty -Path $registry -Name InstallLocation -Value $target -PropertyType String -Force | Out-Null
  New-ItemProperty -Path $registry -Name DisplayIcon -Value (Join-Path $target 'DeathNote.exe') -PropertyType String -Force | Out-Null
  New-ItemProperty -Path $registry -Name UninstallString -Value $uninstall -PropertyType String -Force | Out-Null
  New-ItemProperty -Path $registry -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
  New-ItemProperty -Path $registry -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
  $completed=$true
  Write-Host ('SUCCESS: installed Death Note at '+$target) -ForegroundColor Green
  Write-Host 'Application listed in Settings > Apps > Installed apps.'
  Write-Host ('User data preserved at: '+(Join-Path $env:LOCALAPPDATA 'DeathNote\data'))
} finally {
  if(Test-Path $staged){Remove-Item $staged -Recurse -Force -ErrorAction SilentlyContinue}
  if($completed){
    if(Test-Path $backup){Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue}
  } elseif($swapped) {
    # An installation error must not leave the user without the old version.
    # Preserve a failed target if it cannot be removed, and never discard the backup.
    try {
      if(Test-Path $target){Remove-Item $target -Recurse -Force -ErrorAction Stop}
      if(Test-Path $backup){Move-Item -LiteralPath $backup -Destination $target -ErrorAction Stop}
    } catch {
      Write-Host ('Rollback needs manual attention. Previous app is still at: '+$backup) -ForegroundColor Red
    }
  }
}
# Launch only after all installation steps and registration are complete.
Start-Process -FilePath (Join-Path $target 'DeathNote.exe') -WorkingDirectory $target
