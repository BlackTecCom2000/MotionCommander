# Motion Commander — Мультиплатформенная Экосистема Управления Файлами и Накопителями

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platforms: Windows | Linux | macOS](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-0078D4.svg)
![Framework: .NET 8](https://img.shields.io/badge/.NET-8.0%20Multiplatform-512BD4.svg?logo=dotnet)
[![Author: BlackTecCom - Jaborov Daler](https://img.shields.io/badge/Author-BlackTecCom%20--%20Jaborov%20Daler-00F0FF.svg)](https://github.com/BlackTecCom2000)
[![Version: v3.0.0](https://img.shields.io/badge/Version-3.0.0%20Release-10B981.svg)](https://github.com/BlackTecCom2000/MotionCommander/releases)
[![Donate: Visa Direct](https://img.shields.io/badge/Donate-VISA%20Direct-1A1F71.svg?logo=visa&logoColor=white)](#donate)

> **Motion Commander** — это ультра-современная высокопроизводительная кроссплатформенная экосистема для **Windows 10/11**, **Linux (Ubuntu, Debian, Fedora, Arch)** и **macOS (Apple Silicon M1-M4 & Intel)**. 
> Проект объединяет в себе двухбуферный конвейер потокового копирования данных со скоростью свыше 2+ ГБ/с, виртуальный многопоточный архиватор, аппаратный диагностический центр накопителей **Storage Control Center** и консольную утилиту **`motion`**.

---

## 🌟 Архитектура решения

Экосистема спроектирована по принципу модульного разделения ядра и интерфейсов:

```
MotionCommander/
├── src/MotionCommander.Core/    # Чистое кроссплатформенное ядро (.NET 8): StreamingPipeline, BufferPool, ArchiveEngine, StorageProviders
├── src/MotionCommander.Cli/     # Кроссплатформенная консольная утилита 'motion' (Linux, macOS, Windows)
├── Win11CopyDialog/             # Премиальное десктопное приложение Windows 11 (Mica Glass, SuperAdmin Engine, 120 FPS)
└── .github/workflows/           # Автоматическая сборка релизов под все ОС (CI/CD GitHub Actions)
```

---

## 🚀 Возможности кроссплатформенного ядра (Core & CLI)

### 1. 🏎 Конвейер копирования (Full Duplex Streaming Pipeline)
- **Скорость до 5+ ГБ/с**: Прямой стриминг между NVMe/SSD/HDD без аллокаций в LOH через `System.Threading.Channels` и `ArrayPool<byte>`.
- **Двухбуферный конвейер**: Чтение следующего блока происходит параллельно с записью текущего.
- **Детекция узких мест**: Анализ латентности и пропускной способности шины в реальном времени.

### 2. 🖴 Мультиплатформенный аудит накопителей (Storage Providers)
- **Windows**: WMI/CIM, Win32 API, `diskpart override`, `defrag/ReTrim`.
- **Linux**: парсинг `/sys/block`, `lsblk -J`, `smartctl`, `fstrim`, `parted`.
- **macOS**: `diskutil`, `system_profiler SPStorageDataType`, APFS snapshot & TRIM.

### 3. 🗜 Виртуальный архиватор
- Чтение и просмотр содержимого 13 форматов (`.zip`, `.7z`, `.rar`, `.tar`, `.gz`, `.bz2`, `.xz`, `.iso`, `.cab`, `.arj` и др.) без извлечения на диск.
- Многопоточное создание архивов.

---

## 💻 Использование консольной утилиты `motion` (Linux, macOS, Windows)

Консольная утилита `motion` создана для серверов, терминалов и быстрой работы с дисками в любой ОС:

```bash
# Сверхбыстрое копирование файлов с анимированным прогресс-баром
motion copy bigfile.iso /mnt/backup/bigfile.iso

# Аудит всех физических накопителей (NVMe, SSD, HDD, USB) и разделов
motion disks

# Отчет о здоровье диска, температуре и ресурсе ячеек S.M.A.R.T.
motion smart 0

# Аппаратный бенчмарк скорости чтения, записи и IOPS накопителя
motion bench /mnt/nvme

# Архивация файлов в ZIP
motion zip backup.zip file1.txt folder2/

# Распаковка архива
motion extract archive.7z ./output/

# Информация о системе и лицензии
motion info
```

---

## 📥 Установка и запуск

### Windows (GUI)
1. Скачайте **[MotionCommander-v3.0.0-Portable.zip](https://github.com/BlackTecCom2000/MotionCommander/releases)**.
2. Распакуйте архив и запустите `Win11CopyDialog.exe`.

### Linux (x64 / ARM64)
```bash
# Скачайте архив для Linux
wget https://github.com/BlackTecCom2000/MotionCommander/releases/download/v3.0.0/motion-linux-x64.tar.gz
tar -xzf motion-linux-x64.tar.gz
chmod +x motion
sudo mv motion /usr/local/bin/

# Проверка работы
motion info
```

### macOS (Apple Silicon / Intel)
```bash
# Скачайте архив для macOS
curl -LO https://github.com/BlackTecCom2000/MotionCommander/releases/download/v3.0.0/motion-macos-arm64.tar.gz
tar -xzf motion-macos-arm64.tar.gz
chmod +x motion
sudo mv motion /usr/local/bin/

# Проверка работы
motion info
```

---

## 🛠 Сборка из исходного кода

Требования: установленный [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
# Клонирование репозитория
git clone https://github.com/BlackTecCom2000/MotionCommander.git
cd MotionCommander

# Сборка всего решения (Core, CLI, Windows GUI)
dotnet build MotionCommander.sln -c Release

# Публикация кроссплатформенного CLI под вашу текущую ОС
dotnet publish src/MotionCommander.Cli/MotionCommander.Cli.csproj -c Release -o ./dist-cli
```

---

<a id="donate"></a>
## 💙 Поддержка проекта (Support Motion Commander)

Если вам нравится **Motion Commander** и вы хотите поддержать независимого разработчика и фрилансера, вы можете совершить добровольный донат напрямую на карты **VISA**:

| Банк | Платежная система | Номер карты для перевода |
| :--- | :--- | :--- |
| 🇹🇯 **Alif Bank** | **VISA** | `4444 8888 1022 6013` |
| 🇹🇯 **DC Bank**   | **VISA** | `4713 3800 2165 1431` |

> 💳 Переводы принимаются напрямую по номеру карты через мобильные приложения любых банков и международную систему **VISA Direct**.  
> **Автор / Разработчик:** BlackTecCom — Jaborov Daler

---

## 📜 Лицензия и авторские права

Проект распространяется под свободной лицензией **MIT License**.  
Авторские права:  
**Copyright (c) 2026 BlackTecCom - Jaborov Daler. All rights reserved.**
