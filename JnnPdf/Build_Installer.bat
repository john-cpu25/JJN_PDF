@echo off
REM ==========================================================
REM  Build the JNN PDF installer (Setup.exe)
REM  1) dotnet publish self-contained (no .NET install needed on target PC)
REM  2) compile installer\JnnPdf.iss with Inno Setup 6
REM  Output: installer\Output\JNN_PDF_Setup_<version>.exe
REM ==========================================================
cd /d "%~dp0"

set ISCC=
for %%P in ("%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" "%ProgramFiles%\Inno Setup 6\ISCC.exe") do (
    if exist %%P set ISCC=%%P
)
if not defined ISCC (
    echo Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup
    pause
    exit /b 1
)

echo [1/2] Publishing JNN PDF (self-contained, win-x64)...
if exist "publish\win-x64" rmdir /s /q "publish\win-x64"
dotnet publish JnnPdf.csproj -c Release -r win-x64 --self-contained true -p:DebugType=none -o publish\win-x64 -nologo -v q
if errorlevel 1 (
    echo Publish failed.
    pause
    exit /b 1
)

echo [2/2] Compiling installer...
%ISCC% /Q "installer\JnnPdf.iss"
if errorlevel 1 (
    echo Installer compile failed.
    pause
    exit /b 1
)

echo.
echo Done. Installer is in: %~dp0installer\Output
explorer "%~dp0installer\Output"
