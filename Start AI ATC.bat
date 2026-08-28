@echo off
title MSFS AI ATC
color 0A

:: Find the built exe or use dotnet run as fallback
set EXE=%~dp0bin\Release\net8.0-windows\MsfsAiAtc.exe

if exist "%EXE%" (
    start "" "%EXE%"
) else (
    :: Fall back to dotnet run (works before first release build)
    cd /d "%~dp0"
    dotnet run --project MsfsAiAtc.csproj -c Release --no-build 2>nul
    if %errorlevel% neq 0 (
        dotnet run --project MsfsAiAtc.csproj -c Release
    )
)
