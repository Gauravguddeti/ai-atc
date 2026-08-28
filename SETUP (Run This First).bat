@echo off
title MSFS AI ATC - First Time Setup
color 0B
echo.
echo  =====================================================
echo   MSFS AI ATC  -  First Time Setup
echo  =====================================================
echo.
echo  This will:
echo    1. Check that .NET 8 is installed
echo    2. Download the AI voice (Piper TTS) ~70 MB
echo    3. Build the app
echo.
echo  Your internet connection is needed. Takes 1-3 minutes.
echo.
pause

:: ── Step 1: Check .NET 8 ───────────────────────────────────────────────────
echo.
echo  [1/3] Checking .NET 8...
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo  ERROR: .NET 8 is NOT installed on this PC.
    echo.
    echo  Please install it from:
    echo  https://dotnet.microsoft.com/en-us/download/dotnet/8.0
    echo.
    echo  Download the ".NET 8 Desktop Runtime" for Windows x64.
    echo  After installing, run this setup again.
    echo.
    pause
    exit /b 1
)

for /f "tokens=1" %%v in ('dotnet --version') do set DOTNET_VER=%%v
echo  .NET version: %DOTNET_VER%  OK

:: ── Step 2: Download Piper TTS ────────────────────────────────────────────
echo.
echo  [2/3] Downloading AI voice (Piper TTS)...
echo        This is ~70 MB and may take 1-2 minutes.
echo.
powershell -ExecutionPolicy Bypass -File "%~dp0setup-piper.ps1"
if %errorlevel% neq 0 (
    echo.
    echo  WARNING: Piper download may have failed.
    echo  The app will still run, but there will be no voice audio.
    echo  Try running this setup again with a stable internet connection.
    echo.
    pause
)

:: ── Step 3: Build ─────────────────────────────────────────────────────────
echo.
echo  [3/3] Building the app...
dotnet build "%~dp0MsfsAiAtc.csproj" -c Release --nologo -v quiet
if %errorlevel% neq 0 (
    echo.
    echo  ERROR: Build failed.
    echo  Please send a screenshot of this window to Gaurav.
    echo.
    pause
    exit /b 1
)

echo.
echo  =====================================================
echo   Setup complete!
echo.
echo   You can now double-click "Start AI ATC.bat" 
echo   to launch the app anytime.
echo  =====================================================
echo.
pause
