@echo off
title MSFS AI ATC
color 0A

set ROOT=%~dp0
set EXE=%ROOT%dist\MsfsAiAtc.exe

if not exist "%EXE%" (
    echo.
    echo  ERROR: App not found at dist\MsfsAiAtc.exe
    echo.
    echo  Please double-click "SETUP (Run This First).bat" first,
    echo  then try again.
    echo.
    pause
    exit /b 1
)

:: Set working directory to repo root so the app finds .env and piper/
:: (The exe is in dist\ but all data files are in the root folder)
cd /d "%ROOT%"

:: Launch the app and wait for it to exit
:: If it crashes, this window stays open so the error is visible
"%EXE%"

if %errorlevel% neq 0 (
    echo.
    echo  =====================================================
    echo   AI ATC exited with an error (code: %errorlevel%)
    echo.
    echo   A crash log has been saved to: airatc.log
    echo   Send that file to Gaurav to get it fixed.
    echo  =====================================================
    echo.
    pause
)
