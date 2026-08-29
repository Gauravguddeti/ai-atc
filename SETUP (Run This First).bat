@echo off
title MSFS AI ATC - First Time Setup
color 0B
echo.
echo  =====================================================
echo   MSFS AI ATC  -  First Time Setup
echo  =====================================================
echo.
echo  This will:
echo    1. Check that .NET 8 Runtime is installed
echo    2. Download the AI voice (Piper TTS) ~70 MB
echo.
echo  Your internet connection is needed. Takes 1-3 minutes.
echo  The app itself is already pre-built - no SDK needed!
echo.
pause

:: ── Step 1: Check .NET 8 Runtime ───────────────────────────────────────────
echo.
echo  [1/2] Checking .NET 8 Runtime...

:: The .NET Desktop Runtime does NOT add dotnet.exe to PATH by default.
:: So we check the runtime folder directly — most reliable method.

set RUNTIME_OK=0

:: Method A: Check runtime folder (covers Runtime-only installs)
for /d %%d in ("%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\8.*") do (
    set RUNTIME_OK=1
)

:: Method B: dotnet.exe absolute path exists
if %RUNTIME_OK%==0 (
    if exist "%ProgramFiles%\dotnet\dotnet.exe" (
        "%ProgramFiles%\dotnet\dotnet.exe" --list-runtimes 2>nul | findstr /i "WindowsDesktop.*8\." >nul
        if %errorlevel%==0 set RUNTIME_OK=1
    )
)

:: Method C: dotnet is in PATH (SDK/full install)
if %RUNTIME_OK%==0 (
    dotnet --list-runtimes 2>nul | findstr /i "WindowsDesktop.*8\." >nul
    if %errorlevel%==0 set RUNTIME_OK=1
)

if %RUNTIME_OK%==0 (
    echo.
    echo  ERROR: .NET 8 Desktop Runtime is NOT detected.
    echo.
    echo  Please install it from:
    echo  https://dotnet.microsoft.com/en-us/download/dotnet/8.0
    echo.
    echo  Click: ".NET Desktop Runtime 8.x.x" then "Windows x64"
    echo  Run the installer, then run this setup again.
    echo.
    pause
    exit /b 1
)

echo  .NET 8 Runtime detected  OK

:: ── Step 2: Download Piper TTS ─────────────────────────────────────────────
echo.
echo  [2/2] Downloading AI voice (Piper TTS)...
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

echo.
echo  =====================================================
echo   Setup complete!
echo.
echo   You can now double-click "Start AI ATC.bat"
echo   to launch the app anytime.
echo  =====================================================
echo.
pause
