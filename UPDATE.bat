@echo off
title MSFS AI ATC - Auto Updater
color 0A
setlocal EnableDelayedExpansion

echo.
echo  =====================================================
echo    MSFS AI ATC ^| One-Click Auto Updater
echo  =====================================================
echo.

:: ── 1. Make sure the app isn't running (it locks the DLL) ─────────────────────
tasklist /FI "IMAGENAME eq MsfsAiAtc.exe" 2>nul | find /I "MsfsAiAtc.exe" >nul
if %errorlevel% equ 0 (
    echo  [!] MSFS AI ATC is currently running.
    echo      Please close it first, then run this updater again.
    echo.
    pause
    exit /b 1
)

:: ── 2. Get the latest commit SHA from GitHub (cheap API call, no ZIP download) ─
echo  Checking for updates...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "try { $r = Invoke-WebRequest -Uri 'https://api.github.com/repos/Gauravguddeti/ai-atc/commits/main' -UseBasicParsing -Headers @{'User-Agent'='msfs-ai-atc-updater'}; $json = $r.Content | ConvertFrom-Json; Write-Host $json.sha } catch { Write-Host 'ERROR'; exit 1 }" > "%TEMP%\ai-atc-remote-sha.txt"
if %errorlevel% neq 0 (
    echo  [ERROR] Could not reach GitHub. Check your internet connection.
    pause
    exit /b 1
)

set /p REMOTE_SHA=<"%TEMP%\ai-atc-remote-sha.txt"
if "%REMOTE_SHA%"=="ERROR" (
    echo  [ERROR] GitHub API returned an error.
    pause
    exit /b 1
)

:: ── 3. Read local version (stored by updater after each run) ──────────────────
set LOCAL_SHA=none
if exist "%~dp0.update-sha" set /p LOCAL_SHA=<"%~dp0.update-sha"

:: Compare (first 12 chars is plenty for a unique match)
set REMOTE_SHORT=!REMOTE_SHA:~0,12!
set LOCAL_SHORT=!LOCAL_SHA:~0,12!

if "!REMOTE_SHORT!"=="!LOCAL_SHORT!" (
    echo.
    echo  =====================================================
    echo    Already up to date!  (version !REMOTE_SHORT!)
    echo  =====================================================
    echo.
    echo   No changes since your last update. Nothing to do.
    echo.
    pause
    exit /b 0
)

echo  New version found:  !REMOTE_SHORT!
if "!LOCAL_SHORT!"=="none" (
    echo  Local version:      ^(first install^)
) else (
    echo  Local version:      !LOCAL_SHORT!
)
echo.

:: ── 4. Download the ZIP ───────────────────────────────────────────────────────
set TMPDIR=%TEMP%\ai-atc-update-%RANDOM%
mkdir "%TMPDIR%" 2>nul

echo  Downloading update...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "try { Invoke-WebRequest -Uri 'https://github.com/Gauravguddeti/ai-atc/archive/refs/heads/main.zip' -OutFile '%TMPDIR%\update.zip' -UseBasicParsing; Write-Host 'OK' } catch { Write-Host 'FAIL'; exit 1 }" > "%TEMP%\ai-atc-dl.txt"
set /p DL_RESULT=<"%TEMP%\ai-atc-dl.txt"
if "%DL_RESULT%"=="FAIL" (
    echo  [ERROR] Download failed. Check your internet.
    rmdir /S /Q "%TMPDIR%" 2>nul
    pause
    exit /b 1
)

:: ── 5. Extract ────────────────────────────────────────────────────────────────
echo  Extracting...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "Expand-Archive -Path '%TMPDIR%\update.zip' -DestinationPath '%TMPDIR%\src' -Force"
if %errorlevel% neq 0 (
    echo  [ERROR] Could not extract ZIP.
    rmdir /S /Q "%TMPDIR%" 2>nul
    pause
    exit /b 1
)

:: ── 6. Copy ONLY the dist\ binaries (exe, dll, pdb) — nothing else ───────────
:: SAFE files to update (never touch .env, piper\, data\, *.log, *.txt)
echo  Installing updated binaries...

:: Copy exe + dll + pdb individually (never wildcard — explicit safety)
for %%F in (MsfsAiAtc.exe MsfsAiAtc.dll MsfsAiAtc.pdb) do (
    if exist "%TMPDIR%\src\ai-atc-main\dist\%%F" (
        copy /Y "%TMPDIR%\src\ai-atc-main\dist\%%F" "%~dp0dist\%%F" >nul
        if !errorlevel! neq 0 (
            echo  [ERROR] Could not update dist\%%F — is the app still running?
            rmdir /S /Q "%TMPDIR%" 2>nul
            pause
            exit /b 1
        )
    )
)

:: Copy any runtimes that live in dist\ (e.g. updated .runtimeconfig.json)
for %%F in (MsfsAiAtc.runtimeconfig.json MsfsAiAtc.deps.json) do (
    if exist "%TMPDIR%\src\ai-atc-main\dist\%%F" (
        copy /Y "%TMPDIR%\src\ai-atc-main\dist\%%F" "%~dp0dist\%%F" >nul
    )
)

:: ── 7. Update root .bat files (START / SETUP / UPDATE itself may have changed) ─
echo  Updating launcher scripts...
for %%F in ("%TMPDIR%\src\ai-atc-main\*.bat") do (
    copy /Y "%%F" "%~dp0" >nul
)

:: ── 8. Cleanup ────────────────────────────────────────────────────────────────
rmdir /S /Q "%TMPDIR%" 2>nul
del "%TEMP%\ai-atc-remote-sha.txt" 2>nul
del "%TEMP%\ai-atc-dl.txt" 2>nul

:: ── 9. Save new SHA so next run can check ────────────────────────────────────
echo !REMOTE_SHA!>"%~dp0.update-sha"

:: ── 10. Final report ─────────────────────────────────────────────────────────
echo.
echo  =====================================================
echo    Update complete!  (version !REMOTE_SHORT!)
echo  =====================================================
echo.
echo   WHAT WAS UPDATED:
echo    ^> dist\MsfsAiAtc.exe  (main application)
echo    ^> dist\MsfsAiAtc.dll  (app logic)
echo    ^> Launcher .bat files
echo.
echo   WHAT WAS NOT TOUCHED:
echo    ^> Your .env  (API keys safe)
echo    ^> piper\     (voice engine safe)
echo    ^> data\      (airport database safe — no re-setup needed)
echo    ^> *.log      (flight logs safe)
echo.
echo   Just run:  Start AI ATC.bat
echo  =====================================================
echo.
pause
