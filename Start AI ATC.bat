@echo off
title MSFS AI ATC
color 0A

set EXE=%~dp0dist\MsfsAiAtc.exe

if exist "%EXE%" (
    start "" "%EXE%"
    exit /b 0
)

echo.
echo  ERROR: App not found at dist\MsfsAiAtc.exe
echo.
echo  Please double-click "SETUP (Run This First).bat" first,
echo  then try again.
echo.
pause
exit /b 1
