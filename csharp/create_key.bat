@echo off
title pika op - owner license issuer
if exist "%~dp0owner\pika_op_key_issuer.exe" (
    start "" "%~dp0owner\pika_op_key_issuer.exe"
) else (
    echo Owner issuer EXE is missing. Rebuild the owner tool first.
    pause
)
