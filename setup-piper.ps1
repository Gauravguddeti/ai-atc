# MSFS AI ATC — Setup Helper Script
# Downloads Piper TTS binaries and voice model.
# Run from the repo root: .\setup-piper.ps1

param(
    [string]$VoiceName = "en_US-lessac-medium",
    [string]$PiperVersion = "2023.11.14-2"
)

$ErrorActionPreference = "Stop"
$BaseDir = Split-Path -Parent $PSCommandPath
$PiperDir = Join-Path $BaseDir "piper"
$ModelsDir = Join-Path $PiperDir "models"

Write-Host ""
Write-Host "  MSFS AI ATC - Piper TTS Setup" -ForegroundColor Cyan
Write-Host "  ================================" -ForegroundColor Cyan
Write-Host ""

New-Item -ItemType Directory -Force -Path $PiperDir | Out-Null
New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null

$PiperExe = Join-Path $PiperDir "piper.exe"
$PiperZipUrl = "https://github.com/rhasspy/piper/releases/download/$PiperVersion/piper_windows_amd64.zip"
$PiperZipPath = Join-Path $PiperDir "piper_windows.zip"

if (Test-Path $PiperExe) {
    Write-Host "  [1/3] Piper binary already present" -ForegroundColor Green
} else {
    Write-Host "  [1/3] Downloading Piper binary from GitHub..." -ForegroundColor Yellow
    Write-Host "        $PiperZipUrl"
    Invoke-WebRequest -Uri $PiperZipUrl -OutFile $PiperZipPath -UseBasicParsing
    Write-Host "  [1/3] Extracting..." -ForegroundColor Yellow
    Expand-Archive -Path $PiperZipPath -DestinationPath $PiperDir -Force
    Remove-Item $PiperZipPath -ErrorAction SilentlyContinue
    Write-Host "  [1/3] Piper binary installed" -ForegroundColor Green
}

$VoiceParts = $VoiceName.Split("-")
$LangCode = $VoiceParts[0]
$Lang2 = $LangCode.Split("_")[0]
$Speaker = if ($VoiceParts.Length -gt 1) { $VoiceParts[1] } else { "default" }
$Quality = if ($VoiceParts.Length -gt 2) { $VoiceParts[2] } else { "medium" }

$ModelBaseUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0"
$OnnxUrl = "$ModelBaseUrl/$Lang2/$LangCode/$Speaker/$Quality/$VoiceName.onnx"
$JsonUrl  = "$ModelBaseUrl/$Lang2/$LangCode/$Speaker/$Quality/$VoiceName.onnx.json"

$OnnxPath = Join-Path $ModelsDir "$VoiceName.onnx"
$JsonPath  = Join-Path $ModelsDir "$VoiceName.onnx.json"

if (Test-Path $OnnxPath) {
    Write-Host "  [2/3] Voice model already present" -ForegroundColor Green
} else {
    Write-Host "  [2/3] Downloading voice model: $VoiceName..." -ForegroundColor Yellow
    Write-Host "        $OnnxUrl"
    Invoke-WebRequest -Uri $OnnxUrl -OutFile $OnnxPath -UseBasicParsing
    Write-Host "  [2/3] Voice model downloaded" -ForegroundColor Green
}

if (Test-Path $JsonPath) {
    Write-Host "  [3/3] Voice config already present" -ForegroundColor Green
} else {
    Write-Host "  [3/3] Downloading voice config..." -ForegroundColor Yellow
    try {
        Invoke-WebRequest -Uri $JsonUrl -OutFile $JsonPath -UseBasicParsing
        Write-Host "  [3/3] Voice config downloaded" -ForegroundColor Green
    } catch {
        Write-Host "  [3/3] Voice config not required, skipping" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "  Setup complete!" -ForegroundColor Green
Write-Host "  Piper:  $PiperExe"
Write-Host "  Model:  $OnnxPath"
Write-Host ""
Write-Host "  Now run: dotnet run" -ForegroundColor Cyan
Write-Host ""
