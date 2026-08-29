@echo off
title MSFS AI ATC
color 0A

:: Find the built exe (created by SETUP bat)
set EXE=%~dp0bin\Release\net8.0-windows\MsfsAiAtc.exe

if exist "%EXE%" (
    start "" "%EXE%"
    exit /b 0
)

:: Exe not found — setup hasn't been run yet
echo.
echo  The app has not been set up yet.
echo  Please double-click "SETUP (Run This First).bat" first.
echo.
pause
exit /b 1
