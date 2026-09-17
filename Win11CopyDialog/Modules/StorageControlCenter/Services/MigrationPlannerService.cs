using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

/// <summary>
/// Service responsible for generating a dry-run plan for OS Migration.
/// Evaluates target disk, available space, existing partitions, and determines
/// if the operation is safe or destructive.
/// </summary>
public static class MigrationPlannerService
{
    // Approximately 60GB minimum for a Windows 11 installation + overhead
    private const long MIN_REQUIRED_SPACE_MB = 60 * 1024;

    public static async Task<MigrationPlan> GeneratePlanAsync(int targetDiskNumber, StorageDisk targetDisk, MigrationMode mode, CancellationToken ct = default)
    {
        var plan = new MigrationPlan
        {
            Mode = mode,
            TargetDiskNumber = targetDiskNumber,
            TargetDiskModel = targetDisk?.Model ?? "Unknown Disk",
            RequiredSpaceMB = MIN_REQUIRED_SPACE_MB
        };

        if (targetDisk == null)
        {
            plan.Warnings.Add("Целевой диск не найден или отключен.");
            return plan;
        }

        // 1. System Disk Protection (Safety Guard)
        // If the target disk contains the active OS, immediately block.
        if (targetDisk.Partitions.Any(p => p.IsSystem || p.IsBoot || p.DriveLetter.Equals("C", StringComparison.OrdinalIgnoreCase)))
        {
            plan.Warnings.Add("КРИТИЧЕСКАЯ ОШИБКА: Выбранный диск является текущим системным диском Windows. Миграция заблокирована.");
            return plan;
        }

        // 2. Evaluate space and mode
        long totalCapacityMB = targetDisk.TotalSizeBytes / (1024 * 1024);
        
        if (totalCapacityMB < plan.RequiredSpaceMB)
        {
            plan.Warnings.Add($"Недостаточно места на физическом диске. Требуется минимум {plan.RequiredSpaceMB / 1024} ГБ, доступно {totalCapacityMB / 1024} ГБ.");
            return plan;
        }

        if (mode == MigrationMode.FullDiskClone)
        {
            plan.IsDestructive = true;
            plan.PlannedSteps.Add($"ВНИМАНИЕ: Все данные на диске {plan.TargetDiskModel} (Диск {plan.TargetDiskNumber}) будут безвозвратно удалены.");
            plan.PlannedSteps.Add("1. Полная очистка диска (DiskPart Clean).");
            plan.PlannedSteps.Add("2. Инициализация диска в формат GPT.");
            plan.PlannedSteps.Add("3. Создание системных разделов (EFI, MSR).");
            plan.PlannedSteps.Add("4. Создание основного раздела Windows (на всё доступное пространство).");
            plan.PlannedSteps.Add("5. Создание теневой копии (VSS) текущей системы.");
            plan.PlannedSteps.Add("6. Копирование файлов ОС (Robocopy).");
            plan.PlannedSteps.Add("7. Установка загрузчика (bcdboot).");
        }
        else // SafeMigration
        {
            plan.IsDestructive = false;
            
            // Check if disk is completely empty (no partitions)
            if (!targetDisk.Partitions.Any())
            {
                plan.PlannedSteps.Add("Диск не размечен. Будет выполнена безопасная разметка всего диска.");
                plan.PlannedSteps.Add("1. Инициализация диска в формат GPT.");
                plan.PlannedSteps.Add("2. Создание системных разделов (EFI, MSR).");
                plan.PlannedSteps.Add("3. Создание основного раздела Windows (на всё доступное пространство).");
            }
            else
            {
                // We need unallocated space. For simplicity in this demo planner, if there are partitions,
                // we warn that safe migration requires unallocated space or we cannot proceed automatically yet.
                // A real planner would use 'diskpart shrink' or check unallocated gaps.
                plan.Warnings.Add("Безопасная миграция на размеченный диск (с существующими разделами) в данный момент требует ручного высвобождения неразмеченного пространства. Пожалуйста, используйте 'Управление дисками', чтобы освободить минимум 60 ГБ.");
                return plan;
            }

            plan.PlannedSteps.Add("4. Создание теневой копии (VSS) текущей системы.");
            plan.PlannedSteps.Add("5. Копирование файлов ОС (Robocopy).");
            plan.PlannedSteps.Add("6. Установка загрузчика (bcdboot).");
        }

        return plan;
    }
}
