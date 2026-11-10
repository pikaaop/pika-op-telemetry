@echo off
setlocal
set "appdir=%~dp0csharp\release"
if exist "%appdir%\pika_op.exe" goto verify
set "appdir=%~dp0"
if exist "%appdir%\pika_op.exe" goto verify
set "appdir=D:\Academpic paper\csharp\release"
:verify
if not exist "%appdir%\pika_op.exe" goto missing
if not exist "%appdir%\release.manifest.json" goto missing
if not exist "%appdir%\release.manifest.sig" goto missing
if /i "%~1"=="--check" (
    echo Launcher check passed: "%appdir%"
    exit /b 0
)
echo Launching the existing unfinished build. Current source is not rebuilt.
start "" /D "%appdir%" "%appdir%\pika_op.exe"
exit /b 0
:missing
echo Required release files are missing from:
echo "%appdir%"
echo Keep pika_op.exe, release.manifest.json, and release.manifest.sig together.
pause
exit /b 1
