@echo off
REM ==========================================================
REM  Launch JNN PDF. Builds the app first if the exe is missing.
REM ==========================================================
cd /d "%~dp0"
set EXE=bin\Release\net8.0-windows10.0.19041.0\win-x64\JnnPdf.exe
if exist "build\JnnPdf.exe" set EXE=build\JnnPdf.exe
if not exist "%EXE%" (
    echo Building JNN PDF for the first time, please wait...
    dotnet build -c Release -nologo -v q
    if errorlevel 1 (
        echo Build failed. Make sure the .NET 8 SDK is installed: https://dotnet.microsoft.com/download/dotnet/8.0
        pause
        exit /b 1
    )
)
start "" "%EXE%" %*
