@echo off
cd /d "%~dp0"
taskkill /F /IM DpiTunnel.exe >nul 2>&1
taskkill /F /IM goodbyedpi.exe >nul 2>&1
taskkill /F /IM ciadpi.exe >nul 2>&1
taskkill /F /IM winws.exe >nul 2>&1
timeout /t 2 /nobreak >nul
if exist DpiTunnel_new.exe (
    copy /Y DpiTunnel_new.exe DpiTunnel.exe >nul
    if not errorlevel 1 del DpiTunnel_new.exe >nul 2>&1
)
start "" DpiTunnel.exe
