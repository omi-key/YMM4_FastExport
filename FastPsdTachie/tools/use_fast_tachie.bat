@echo off
setlocal
if "%~1"=="" (echo Drag and drop a .ymmp file onto this bat. & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0switch_tachie.ps1" -Path "%~1" -To fast
pause
