@echo off
rem guardAI test launcher - double click to start, or pass args: test.cmd E:\some\project init check
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0test.ps1" %*
pause
