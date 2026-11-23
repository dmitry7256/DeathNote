@echo off
setlocal
cd /d "%~dp0"
echo Building Death Note 3.00 Setup.exe (Windows x64)...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0BUILD_INSTALLER.ps1"
set "RESULT=%ERRORLEVEL%"
echo.
if not "%RESULT%"=="0" echo Build failed. Check INSTALL_COMPILER.log or install Inno Setup 6.
if "%RESULT%"=="0" echo Ready: DeathNote_Setup_3.00_x64.exe
pause
exit /b %RESULT%
