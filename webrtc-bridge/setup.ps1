# WebRTC Bridge Setup Script
# Downloads required JavaScript dependencies

Write-Host "Setting up WebRTC Bridge dependencies..." -ForegroundColor Cyan

$jsDir = Join-Path $PSScriptRoot "js"

# Download janus.js from official Janus Gateway repo
Write-Host "Downloading janus.js..." -ForegroundColor Yellow
$janusUrl = "https://raw.githubusercontent.com/meetecho/janus-gateway/master/html/janus.js"
$janusPath = Join-Path $jsDir "janus.js"
try {
    Invoke-WebRequest -Uri $janusUrl -OutFile $janusPath -UseBasicParsing
    Write-Host "  janus.js downloaded successfully" -ForegroundColor Green
} catch {
    Write-Host "  Failed to download janus.js: $_" -ForegroundColor Red
}

# Download adapter.js from WebRTC project
Write-Host "Downloading adapter.js..." -ForegroundColor Yellow
$adapterUrl = "https://webrtc.github.io/adapter/adapter-latest.js"
$adapterPath = Join-Path $jsDir "adapter.js"
try {
    Invoke-WebRequest -Uri $adapterUrl -OutFile $adapterPath -UseBasicParsing
    Write-Host "  adapter.js downloaded successfully" -ForegroundColor Green
} catch {
    Write-Host "  Failed to download adapter.js: $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "Setup complete!" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "1. Start Janus Gateway: cd docker && docker-compose up -d" -ForegroundColor Gray
Write-Host "2. Configure 3CX extension 900 to point to Janus SIP" -ForegroundColor Gray
Write-Host "3. Open index.html in a browser (use a local server for best results)" -ForegroundColor Gray
Write-Host "4. Configure connection settings and connect" -ForegroundColor Gray
