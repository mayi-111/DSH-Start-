@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
echo ================================================
echo   DSH launcher - one click build
echo   (rebuild icon + exe, install, refresh shortcut)
echo ================================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
set "RC=%ERRORLEVEL%"
echo.
if "%RC%"=="0" (
  echo [OK] Build finished. The desktop shortcut "DSH" is up to date.
) else (
  echo [FAILED] exit code %RC% - see the messages above.
)
echo.
pause
