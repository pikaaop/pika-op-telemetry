@echo off
setlocal
title pika op
cd /d "%~dp0"
where python >nul 2>&1
if errorlevel 1 (
    echo Python was not found. Install Python and enable Add Python to PATH.
    pause
    exit /b 1
)
python -c "import pymem, tkinter, win32gui" >nul 2>&1
if errorlevel 1 (
    python -m pip install pymem pywin32
    if errorlevel 1 (
        echo Dependency installation failed.
        pause
        exit /b 1
    )
)
echo Starting pika op. Start your local debug session first.
python research_ui.py
pause
