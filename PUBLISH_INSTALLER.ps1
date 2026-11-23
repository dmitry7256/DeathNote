# Publishes a self-contained .NET 10 WPF application for an installer.
# No runtime installation is required on END USER computers.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File PUBLISH_INSTALLER.ps1
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $MyInvocation.MyCommand.Path
$project=Join-Path $root 'DeathNote.Wpf\DeathNote.Wpf.csproj'
$payload=Join-Path $root 'published-installer'
$log=Join-Path $root 'INSTALL_COMPILER.log'
if(-not [Environment]::Is64BitOperatingSystem){throw 'Windows x64 is required.'}
$dotnet=$null
$cmd=Get-Command dotnet.exe -ErrorAction SilentlyContinue
if($cmd){
  $list=& $cmd.Source --list-sdks
  if($LASTEXITCODE -eq 0 -and ($list | Where-Object {$_ -match '^10\.'})){$dotnet=$cmd.Source}
}
if(-not $dotnet){
  $local=Join-Path $root '.dotnet-sdk\dotnet.exe'
  if(Test-Path $local){
    $list=& $local --list-sdks
    if($LASTEXITCODE -eq 0 -and ($list | Where-Object {$_ -match '^10\.'})){$dotnet=$local}
  }
}
if(-not $dotnet){throw 'A .NET 10 SDK is required on the BUILDER computer. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 or use BUILD_ONE_CLICK.bat to download it.'}
Write-Host ('SDK: '+(& $dotnet --version)) -ForegroundColor Cyan
# Remove the previous payload before publishing, so upgrades never ship stale files.
if(Test-Path $payload){Remove-Item $payload -Force -Recurse}
$oldPreference=$ErrorActionPreference
$ErrorActionPreference='Continue'
try{
  $output=& $dotnet publish $project -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' '-p:PublishReadyToRun=false' '-p:DebugType=None' '-p:DebugSymbols=false' '-p:UseAppHost=true' -o $payload '-v:minimal' 2>&1
  $result=$LASTEXITCODE
}finally{$ErrorActionPreference=$oldPreference}
@($output) | ForEach-Object {$_.ToString()} | Set-Content -Path $log -Encoding UTF8
if($result -ne 0){
  @($output) | Select-Object -Last 40 | ForEach-Object {Write-Host $_.ToString()}
  throw ('WPF publish failed. Full compiler output: '+$log)
}
$exe=Join-Path $payload 'DeathNote.exe'
if(-not (Test-Path $exe)){throw 'Build completed without DeathNote.exe.'}
# A private database must never be embedded in the installable payload.
$leaks=@(Get-ChildItem -Path $payload -Recurse -File | Where-Object {
  $_.Extension -in @('.db','.sqlite','.sqlite3') -or $_.Name -match '\.(db|sqlite|sqlite3)-(wal|shm)$'
})
if($leaks.Count){throw 'Personal database files were found in the installer payload. Distribution stopped.'}
Write-Host ('Installer payload built: '+$payload) -ForegroundColor Green
