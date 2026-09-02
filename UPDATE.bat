@echo off
title MSFS AI ATC - Auto Updater
color 0A

echo.
echo  =========================================
echo   MSFS AI ATC - One-Click Auto Updater
echo  =========================================
echo.
echo  Downloading the latest version...
echo  (Your .env, API keys, piper, and data are safe)
echo.

:: Make a temp folder
set TMPDIR=%TEMP%\ai-atc-update-%RANDOM%
mkdir "%TMPDIR%" 2>nul

:: Download the latest ZIP from GitHub
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { Invoke-WebRequest -Uri 'https://github.com/Gauravguddeti/ai-atc/archive/refs/heads/main.zip' -OutFile '%TMPDIR%\update.zip' -UseBasicParsing; Write-Host 'Download OK' } catch { Write-Host 'DOWNLOAD FAILED:' $_.Exception.Message; exit 1 }"
if %errorlevel% neq 0 (
    echo.
    echo  ERROR: Could not download update. Check your internet connection.
    rmdir /S /Q "%TMPDIR%" 2>nul
    pause
    exit /b 1
)

echo  Extracting...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -Path '%TMPDIR%\update.zip' -DestinationPath '%TMPDIR%\src' -Force"
if %errorlevel% neq 0 (
    echo.
    echo  ERROR: Could not extract the downloaded ZIP.
    rmdir /S /Q "%TMPDIR%" 2>nul
    pause
    exit /b 1
)

echo  Installing updated application files...

:: Copy the pre-built dist\ binaries — skip user data
:: The dist\data\ folder (airport CSV) is preserved because it doesn't exist in the repo ZIP
xcopy /E /I /Y "%TMPDIR%\src\ai-atc-main\dist\*" "%~dp0dist\" > nul

:: Copy batch scripts from root (START / SETUP bat may have updated)
for %%F in ("%TMPDIR%\src\ai-atc-main\*.bat") do (
    copy /Y "%%F" "%~dp0" > nul
)

:: Cleanup
rmdir /S /Q "%TMPDIR%" 2>nul

echo.
echo  =========================================
echo   Update complete!
echo.
echo   Your .env and API keys are untouched.
echo   Just run: Start AI ATC.bat
echo  =========================================
echo.
pause
