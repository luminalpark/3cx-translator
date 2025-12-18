@echo off
REM Start 3CX Translation Bridge Client

cd /d "%~dp0client"

echo ========================================
echo 3CX Translation Bridge (SeamlessM4T)
echo ========================================
echo.

REM Check if built
if not exist "src\TranslationBridge\bin\Release\net8.0\win-x64\TranslationBridge.exe" (
    echo Building...
    dotnet build -c Release
)

echo Starting Translation Bridge...
echo.
echo Press Ctrl+C to stop
echo.

dotnet run --project src\TranslationBridge -c Release

pause
