# Start-Foresight.ps1
# Launches the CargoWise Foresight API and web GUI.
# API: http://localhost:5248
# GUI: http://localhost:5248/index.html

$ErrorActionPreference = 'Stop'

Write-Host "Starting CargoWise Foresight..." -ForegroundColor Cyan
Write-Host "API: http://localhost:5248" -ForegroundColor Green
Write-Host "GUI: http://localhost:5248/index.html" -ForegroundColor Green
Write-Host "Press Ctrl+C to stop." -ForegroundColor Yellow
Write-Host ""

dotnet run --project "$PSScriptRoot\src\CargoWise.Foresight.Api"
