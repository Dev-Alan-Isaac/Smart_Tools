@echo off
rem Runs Publish.ps1 without changing the PC's PowerShell policy (it is only bypassed for this one run).
rem   Publish.cmd            builds the zip and the .msi
rem   Publish.cmd -SkipMsi   builds only the zip
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish.ps1" %*
echo.
pause
