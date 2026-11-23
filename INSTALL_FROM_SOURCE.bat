@echo off
setlocal
cd /d "%~dp0"
echo Building and installing Death Note 3.01 for this Windows user...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0INSTALL_FROM_SOURCE.ps1"
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo Installation failed. Current personal data was not intentionally modified.
pause
exit /b %RESULT%
