@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-server.ps1" %*
exit /b %ERRORLEVEL%
