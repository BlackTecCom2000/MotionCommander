@echo off
chcp 65001 >nul
title Установка Motion Commander v3.0 (BlackTecCom)
echo Запуск мастера установки Motion Commander...
powershell -ExecutionPolicy Bypass -File "%~dp0scripts\Install.ps1"
echo.
pause
