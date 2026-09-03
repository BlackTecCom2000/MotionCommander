# Motion Commander — Все-в-одном Файловый Менеджер, Архиватор и Storage Control Center

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows 10/11](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20x64-0078D4.svg?logo=windows)]()
[![Framework: .NET 8 WPF](https://img.shields.io/badge/.NET-8.0%20WPF-512BD4.svg?logo=dotnet)]()
[![Author: BlackTecCom - Jaborov Daler](https://img.shields.io/badge/Author-BlackTecCom%20--%20Jaborov%20Daler-00F0FF.svg)]()
[![Version: v3.0.0](https://img.shields.io/badge/Version-3.0.0%20Release-10B981.svg)]()
[![Sponsor: GitHub Sponsors](https://img.shields.io/badge/Sponsor-GitHub%20Sponsors-ea4aaa.svg?logo=githubsponsors)](https://github.com/sponsors/BlackTecCom2000)

> **Motion Commander** — это ультра-современный, сверхпроизводительный инструмент нового поколения для Windows 10 и 11, объединяющий в себе высокоскоростной конвейер передачи файлов, виртуальный архиватор, профессиональный центр контроля дисков **Storage Control Center** и низкоуровневый менеджер разделов **Disk Partition Manager**.

---

## 🌟 Ключевые возможности

### 1. 🏎 Аппаратный движок копирования (Full-Duplex Streaming Pipeline)
- **Скорость до 5+ ГБ/с**: Прямой стриминг между NVMe/SSD/HDD без аллокаций в Large Object Heap (LOH) через `System.Threading.Channels` и `ArrayPool<byte>`.
- **Двухбуферный конвейер**: Чтение следующего блока происходит параллельно с записью текущего.
- **Анализ узких мест (Realtime Bottleneck Detector)**: Автоматическое определение лимитов шины, контроллера или оперативной памяти.

### 2. 🖴 Storage Control Center (v2.0 Master Engine)
- **Топология и мониторинг**: Полная диагностика NVMe PCIe, SATA SSD, HDD и внешних USB 3.0 накопителей.
- **S.M.A.R.T. Health Score (Грейд A+)**: Аппаратные термопрофили ядра контроллера, остаточный ресурс ячеек NAND и подсчет суммарных записанных терабайт (TBW).
- **Интеллектуальная оптимизация**: Нативный `ReTrim` для SSD-накопителей (с категорическим запретом разрушительной дефрагментации флеш-памяти) и умная кластерная дефрагментация для магнитных HDD.
- **Неразрушающий бенчмарк скорости**: Измерение последовательного и случайного 4K доступа, IOPS и времени отклика контроллера.

### 3. 🗂 Disk Partition Manager (SuperAdmin Engine)
- **Низкоуровневый Override**: Выполнение операций разметки без блокировок и системных отказов доступа (`MI RESULT 2`) за счет интеграции прав `SeManageVolumePrivilege` и движка `diskpart override`.
- **10 профессиональных инструментов**: Создание, безопасное расширение и сжатие томов, форматирование (NTFS/exFAT/FAT32), смена букв и меток дисков, снятие защиты Read-Only и Chkdsk-аудит.
- **Интерактивная карта разделов**: Визуальное представление EFI, MSR, системных томов и нераспределенного пространства в реальном масштабе.
- **Hero Card тома**: Сводная карточка с 4 метрическими блоками параметров тома и аппаратным статусом безопасности.

### 4. 🗜 Виртуальный архиватор (Virtual Archive Engine)
- Мгновенный просмотр содержимого `.zip`, `.7z`, `.rar`, `.tar`, `.gz` без предварительной распаковки на диск.
- Многопоточное сжатие со скоростью до 2,4 ГБ/с.

### 5. ✨ Футуристический интерфейс (Zero-Conflict Architecture)
- Дизайн в стиле Windows 11 Mica Glass с неоновым киберпанк-ореолом и темным/светлым режимами.
- Адаптивная кинетика скроллинга **120 FPS** с тактильным аудио-откликом (Haptic Audio Feedback).
- Архитектура нулевых конфликтов: изолированные элементы управления, исключение наложений и автопозиционирование активного диска `BringIntoView()`.

### 6. 🔄 Встроенная система автообновления (In-App Auto-Updater)
- Проверка наличия новых релизов в 1 клик через раздел «Опции».
- Просмотр списка изменений новой версии (Changelog).
- Фоновая загрузка дистрибутива с индикатором прогресса и автоматическая установка с перезапуском.

---

## 📥 Установка и запуск

### Вариант 1: Готовая портативная версия (Portable)
1. Скачайте архив **[MotionCommander-v3.0.0-Portable.zip](https://github.com/BlackTecCom2000/MotionCommander/releases)**.
2. Распакуйте в любую удобную папку (например, `C:\Program Files\MotionCommander` или на рабочий стол).
3. Запустите `Win11CopyDialog.exe`.

### Вариант 2: Сборка из исходного кода
Требования: [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) и Windows 10/11 x64.

```bash
# Клонируйте репозиторий
git clone https://github.com/BlackTecCom2000/MotionCommander.git
cd MotionCommander

# Сборка Release версии
dotnet build Win11CopyDialog/Win11CopyDialog.csproj -c Release

# Публикация единого исполняемого пакета
dotnet publish Win11CopyDialog/Win11CopyDialog.csproj -c Release -o ./publish
```

---

## 📜 Лицензия и авторские права

**Copyright (c) 2026 BlackTecCom - Jaborov Daler. All rights reserved.**

Продукт распространяется под свободной лицензией [MIT License](LICENSE).
- Программа бесплатна для скачивания, установки и использования как частными пользователями, так и организациями.
- Автор и правообладатель (**BlackTecCom - Jaborov Daler**) сохраняет за собой эксклюзивные права на развитие ядра, архитектурные изменения и публикацию официальных обновлений.

---

## 💖 Поддержка проекта (GitHub Sponsors)

Если вам нравится **Motion Commander** и вы хотите поддержать дальнейшую разработку новых функций, алгоритмических оптимизаций и аппаратных модулей:

[![Sponsor Motion Commander](https://img.shields.io/badge/💖_Спонсировать_проект-GitHub_Sponsors-ea4aaa?style=for-the-badge&logo=githubsponsors)](https://github.com/sponsors/BlackTecCom2000)

Ваша поддержка помогает проекту оставаться полностью бесплатным, развиваться и получать регулярные обновления!

---

## 👨‍💻 Автор и контакты

- **Разработчик**: Jaborov Daler (BlackTecCom)
- **GitHub**: [@BlackTecCom2000](https://github.com/BlackTecCom2000)
- **Email**: djaborov2000@gmail.com
- **Репозиторий проекта**: [https://github.com/BlackTecCom2000/MotionCommander](https://github.com/BlackTecCom2000/MotionCommander)
