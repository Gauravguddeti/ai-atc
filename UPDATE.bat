@echo off
echo MSFS AI ATC - Auto Updater
echo ==========================
echo Downloading latest version from GitHub...
powershell -Command "Invoke-WebRequest -Uri 'https://github.com/Gauravguddeti/ai-atc/archive/refs/heads/main.zip' -OutFile 'update.zip'"

echo Extracting update...
powershell -Command "Expand-Archive -Path 'update.zip' -DestinationPath 'update_temp' -Force"

echo Installing update (preserving .env and piper models)...
xcopy /s /y /q "update_temp\ai-atc-main\*" .\

echo Cleaning up...
del /q update.zip
rmdir /s /q update_temp

echo.
echo Rebuilding the application...
dotnet publish MsfsAiAtc.csproj -c Release -r win-x64 --no-self-contained -o dist --nologo
if %errorlevel% neq 0 (
    echo [ERROR] Build failed!
    pause
    exit /b %errorlevel%
)
echo.
echo Update successful! You can now run Start AI ATC.bat
pause
