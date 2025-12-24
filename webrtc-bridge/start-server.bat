@echo off
title 3CX Translation Bridge - Web Server
cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -File "%~dp0start-server.ps1"
pause
