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

:: The .NET Desktop Runtime does NOT add dotnet.exe to PATH.
:: So we check two ways:
::   A) dotnet.exe at its known absolute path  C:\Program Files\dotnet\dotnet.exe
::   B) The runtime folder itself exists        C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\8.*
::   C) dotnet is in PATH (SDK install)        dotnet --version

set DOTNET_EXE=""

:: Method A: absolute path (works for Runtime-only installs)
if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    set DOTNET_EXE="%ProgramFiles%\dotnet\dotnet.exe"
    goto :found_dotnet
)

:: Method B: check runtime folder directly
set RUNTIME_FOUND=0
for /d %%d in ("%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\8.*") do (
    set RUNTIME_FOUND=1
    echo  Found runtime at: %%d
)
if %RUNTIME_FOUND%==1 (
    :: Runtime exists but dotnet.exe not found — something unusual, but runtime is there
    :: We still need dotnet.exe to build. Try x86 path.
    if exist "%ProgramFiles(x86)%\dotnet\dotnet.exe" (
        set DOTNET_EXE="%ProgramFiles(x86)%\dotnet\dotnet.exe"
        goto :found_dotnet
    )
    echo.
    echo  Found .NET 8 Runtime but dotnet.exe is missing from expected location.
    echo  This can happen with some Runtime-only installs.
    echo  Trying to continue anyway...
    goto :found_dotnet
)

:: Method C: dotnet is in PATH (full SDK install)
dotnet --version >nul 2>&1
if %errorlevel%==0 (
    set DOTNET_EXE=dotnet
    goto :found_dotnet
)

:: Nothing found
echo.
echo  ERROR: .NET 8 Desktop Runtime is NOT detected on this PC.
echo.
echo  Please install it from:
echo  https://dotnet.microsoft.com/en-us/download/dotnet/8.0
echo.
echo  Click: ".NET Desktop Runtime 8.x.x" then "Windows x64"
echo  Run the installer, then run this setup again.
echo.
pause
exit /b 1

:found_dotnet
echo  .NET 8 detected  OK

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

:: Try the absolute path first, then fall back to PATH
if %DOTNET_EXE%=="" (
    set DOTNET_EXE=dotnet
)

%DOTNET_EXE% build "%~dp0MsfsAiAtc.csproj" -c Release --nologo -v quiet
if %errorlevel% neq 0 (
    echo.
    echo  ERROR: Build failed.
    echo.
    echo  This usually means dotnet.exe cannot be found.
    echo  Try: close this window, restart your PC, then run setup again.
    echo  If it still fails, send a screenshot of this window to Gaurav.
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
