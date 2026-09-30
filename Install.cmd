@echo off
chcp 65001 >nul
title Установка Motion Commander (BlackTecCom)
echo Запуск мастера установки Motion Commander...
powershell -ExecutionPolicy Bypass -File "%~dp0scripts\Install.ps1"
echo.
pause
