# Produce a distributable single-file Setup.exe via Inno Setup 6.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $root 'PUBLISH_INSTALLER.ps1')
$compiler=$null
$command=Get-Command ISCC.exe -ErrorAction SilentlyContinue
if($command){$compiler=$command.Source}
if(-not $compiler){
  foreach($path in @(
      (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
      (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
      (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))){
    if(Test-Path $path){$compiler=$path;break}
  }
}
if(-not $compiler){
  throw 'Install free Inno Setup 6 on the BUILD computer, then run BUILD_INSTALLER.bat again. Installer users do not need Inno Setup or .NET.'
}
$iss=Join-Path $root 'installer\DeathNote.iss'
Write-Host 'Compiling Windows Setup.exe ...' -ForegroundColor Cyan
& $compiler $iss
if($LASTEXITCODE -ne 0){throw 'Inno Setup compiler failed.'}
$setup=Join-Path $root 'DeathNote_Setup_3.00_x64.exe'
if(-not (Test-Path $setup)){throw 'Expected Setup.exe was not created.'}
Write-Host ('Installer ready: '+$setup) -ForegroundColor Green
Write-Host 'Share ONLY the installer EXE with end users, not the source files or private data.'
