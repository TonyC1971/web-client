@echo off
rem apply_webidentity.bat — Windows wrapper for apply_webidentity.py.
rem Usage:
rem    apply_webidentity.bat
rem    apply_webidentity.bat C:\sphere\Source-X
rem
rem Requires Python 3.8+ on PATH. If you don't have Python:
rem   https://www.python.org/downloads/  (tick "Add to PATH" on install)

setlocal

where python >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Python is not installed or not on PATH.
    echo.
    echo Download Python from https://www.python.org/downloads/
    echo During installation, tick the "Add Python to PATH" checkbox.
    echo.
    pause
    exit /b 1
)

python "%~dp0apply_webidentity.py" %*
set EC=%errorlevel%

if "%~1"=="" (
    echo.
    pause
)

exit /b %EC%
