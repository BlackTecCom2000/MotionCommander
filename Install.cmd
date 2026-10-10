@echo off
chcp 65001 >nul
title Установка и обновление Motion Commander (BlackTecCom)
echo ==========================================================
echo    Motion Commander: Установка и обновление
echo ==========================================================
echo Поиск ранее установленной программы и запуск...
echo.
if exist "%~dp0scripts\Install.ps1" (
    powershell -ExecutionPolicy Bypass -NoProfile -File "%~dp0scripts\Install.ps1" %*
) else if exist "%~dp0Install.ps1" (
    powershell -ExecutionPolicy Bypass -NoProfile -File "%~dp0Install.ps1" %*
) else (
    echo [ОШИБКА] Файл Install.ps1 не найден!
    pause
    exit /b 1
)
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ОШИБКА] Установка или обновление завершились с кодом %ERRORLEVEL%.
)
echo.
pause
