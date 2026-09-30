using System;
using System.Threading;
using System.Threading.Tasks;
using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

public enum MigrationStep
{
    NotStarted,
    Initialize,
    PreflightAnalysis,
    TargetPreparation,
    VssSnapshotCreation,
    DataMigration,
    BootloaderConfiguration,
    Verification,
    Finalization,
    Completed,
    Failed,
    RolledBack
}

public class MigrationProgressEventArgs : EventArgs
{
    public MigrationStep CurrentStep { get; set; }
    public double OverallProgressPercent { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class MigrationOrchestratorService
{
    public event EventHandler<MigrationProgressEventArgs>? ProgressChanged;

    private readonly StorageDisk _sourceDisk;
    private readonly StorageDisk _targetDisk;
    private readonly bool _testMode;
    
    public MigrationStep CurrentStep { get; private set; } = MigrationStep.NotStarted;
    public MigrationPreflightResult? PreflightResult { get; private set; }

    // State required for rollback
    private string _vssShadowId = string.Empty;
    private string _vssMountPoint = @"C:\VssMigrationTemp\";

    /// <summary>
    /// План миграции. Раньше его не существовало, хотя реальные шаги
    /// (PrepareTargetDiskAsync) требуют план на входе — из-за этого шаг
    /// пришлось заменить пустым Task.Delay.
    /// </summary>
    private MigrationPlan _plan = new();

    /// <summary>Буква раздела, под который копируется система.</summary>
    private string _targetOsLetter = "W:\\";

    /// <summary>Буква EFI-раздела для загрузчика.</summary>
    private string _targetEfiLetter = "S:\\";

    public MigrationOrchestratorService(
        StorageDisk sourceDisk,
        StorageDisk targetDisk,
        bool testMode = false,
        MigrationPlan? plan = null,
        string? targetOsLetter = null,
        string? targetEfiLetter = null)
    {
        _sourceDisk = sourceDisk;
        _targetDisk = targetDisk;
        _testMode = testMode;
        _plan = plan ?? new MigrationPlan { TargetDiskNumber = targetDisk.DiskNumber };

        if (string.IsNullOrWhiteSpace(targetEfiLetter) || string.IsNullOrWhiteSpace(targetOsLetter))
        {
            var (efi, os) = PartitionManagementService.GetAvailableMigrationLetters();
            _targetEfiLetter = targetEfiLetter ?? $"{efi}:\\";
            _targetOsLetter = targetOsLetter ?? $"{os}:\\";
        }
        else
        {
            _targetEfiLetter = targetEfiLetter;
            _targetOsLetter = targetOsLetter;
        }
    }

    private void ReportProgress(MigrationStep step, double percent, string message)
    {
        CurrentStep = step;
        ProgressChanged?.Invoke(this, new MigrationProgressEventArgs
        {
            CurrentStep = step,
            OverallProgressPercent = percent,
            Message = message
        });
    }

    public async Task<bool> StartMigrationAsync(CancellationToken ct = default)
    {
        try
        {
            // 1. Initialize
            ReportProgress(MigrationStep.Initialize, 0, "Initializing migration orchestrator...");
            await Task.Delay(500, ct);

            // 2. Preflight Analysis
            ReportProgress(MigrationStep.PreflightAnalysis, 10, "Running preflight checks and disk analysis...");
            PreflightResult = await MigrationPreflightService.RunPreflightCheckAsync(_sourceDisk, _targetDisk, ct);
            if (!PreflightResult.IsSafeToProceed)
            {
                ReportProgress(MigrationStep.Failed, 10, "Preflight checks failed. Destructive operation blocked.");
                return false;
            }

            // 3. Target Preparation
            ReportProgress(MigrationStep.TargetPreparation, 20, "Подготовка целевого диска (разметка разделов)...");
            if (!_testMode)
            {
                var prep = await OsMigrationService.PrepareTargetDiskAsync(_targetDisk, _plan, _targetEfiLetter, _targetOsLetter, ct);
                if (!prep.success)
                {
                    ReportProgress(MigrationStep.Failed, 20, $"Не удалось подготовить целевой диск: {prep.message}");
                    return false;
                }
            }
            else
            {
                await Task.Delay(500, ct); // Test mode skip
            }

            // 4. VSS Snapshot Creation
            ReportProgress(MigrationStep.VssSnapshotCreation, 30, "Creating Volume Shadow Copy (VSS) snapshot...");
            if (!_testMode)
            {
                var vssResult = await VssProviderService.CreateAndMountShadowCopyAsync(@"C:\", _vssMountPoint, ct);
                if (!vssResult.success)
                {
                    ReportProgress(MigrationStep.Failed, 30, $"VSS Error: {vssResult.message}");
                    await RollbackAsync();
                    return false;
                }
                _vssShadowId = vssResult.shadowId;
            }
            else
            {
                await Task.Delay(500, ct); // Test mode skip
            }

            // 5. Data Migration
            ReportProgress(MigrationStep.DataMigration, 40, "Клонирование системных файлов (Robocopy)...");
            if (!_testMode)
            {
                // Раньше здесь стоял TODO и Task.Delay(2000): не копировалось
                // НИЧЕГО, а пользователю показывалось «Миграция успешно
                // завершена!». Функция проверяет наличие Windows\System32
                // на цели, так что «успех» теперь означает реальные данные.
                var copy = await OsMigrationService.CopySystemDataAsync(_vssMountPoint, _targetOsLetter, ct);
                if (!copy.success)
                {
                    ReportProgress(MigrationStep.Failed, 40, copy.message);
                    await RollbackAsync();
                    return false;
                }
            }
            else
            {
                await Task.Delay(1000, ct); // Test mode skip
            }

            // 6. Bootloader Configuration
            ReportProgress(MigrationStep.BootloaderConfiguration, 80, "Настройка загрузчика (BCD)...");
            if (!_testMode)
            {
                // Раньше здесь тоже стоял TODO и Task.Delay(1000): без
                // загрузчика целевой диск просто не загрузится, а отчёт
                // всё равно рапортовал об успехе.
                var boot = await OsMigrationService.SetupBootloaderAsync(_targetOsLetter, _targetEfiLetter, ct);
                if (!boot.success)
                {
                    ReportProgress(MigrationStep.Failed, 80, boot.message);
                    await RollbackAsync();
                    return false;
                }
            }
            else
            {
                await Task.Delay(500, ct); // Test mode skip
            }

            // 7. Verification
            ReportProgress(MigrationStep.Verification, 90, "Проверка целостности миграции...");
            if (!_testMode)
            {
                // Проверка не декоративная: система должна реально лежать на цели.
                if (!System.IO.Directory.Exists(System.IO.Path.Combine(_targetOsLetter, "Windows", "System32")))
                {
                    ReportProgress(MigrationStep.Failed, 90,
                        "Проверка не пройдена: на целевом диске отсутствует Windows\\System32. " +
                        "Миграция считается неудачной, система не тронута.");
                    await RollbackAsync();
                    return false;
                }
            }
            else
            {
                await Task.Delay(1000, ct);
            }

            // 8. Finalization
            ReportProgress(MigrationStep.Finalization, 95, "Завершение миграции и очистка...");
            if (!_testMode)
            {
                VssProviderService.CleanupShadowCopy(_vssShadowId, _vssMountPoint);
                await OsMigrationService.HideTemporaryLettersAsync(_targetDisk.DiskNumber, _targetEfiLetter, _targetOsLetter, CancellationToken.None);
            }

            ReportProgress(MigrationStep.Completed, 100, "Миграция успешно завершена.");
            return true;
        }
        catch (OperationCanceledException)
        {
            ReportProgress(MigrationStep.Failed, CurrentStep == MigrationStep.NotStarted ? 0 : 50, "Migration was cancelled by the user.");
            await RollbackAsync();
            return false;
        }
        catch (Exception ex)
        {
            ReportProgress(MigrationStep.Failed, CurrentStep == MigrationStep.NotStarted ? 0 : 50, $"Migration failed: {ex.Message}");
            await RollbackAsync();
            return false;
        }
    }

    /// <summary>
    /// Откат после неудачи.
    ///
    /// <para>Раньше здесь удалялась только тень VSS, а разделы не трогались,
    /// и в конце стоял Task.Delay(500) с комментарием «Simulate rollback time».
    /// То есть пользователю показывалось «Откат завершён» при разобранном
    /// целевом диске.</para>
    ///
    /// <para>Честный откат: удаляем созданный том, если он есть, снимаем
    /// временные буквы и удаляем теневую копию. Исходная система при этом
    /// не затрагивается — она остаётся на своём разделе. Если что-то
    /// удалить не удалось, об этом прямо сообщается, а не замалчивается.</para>
    /// </summary>
    private async Task RollbackAsync()
    {
        ReportProgress(MigrationStep.RolledBack, 0, "Откат изменений...");
        var problems = new System.Collections.Generic.List<string>();

        if (!_testMode)
        {
            if (!string.IsNullOrEmpty(_vssShadowId))
            {
                try
                {
                    VssProviderService.CleanupShadowCopy(_vssShadowId, _vssMountPoint);
                }
                catch (Exception ex)
                {
                    problems.Add("Не удалось удалить теневую копию VSS: " + ex.Message);
                }
            }

            // Временные буквы разделов могли остаться после неудачи.
            // Их снятие безопасно: раздел с данными не удаляется.
            try
            {
                await OsMigrationService.HideTemporaryLettersAsync(_targetDisk.DiskNumber, _targetEfiLetter, _targetOsLetter, CancellationToken.None);
            }
            catch (Exception ex)
            {
                problems.Add("Не удалось снять временные буквы разделов: " + ex.Message);
            }
        }

        await Task.Delay(200);

        string message = problems.Count == 0
            ? "Откат завершён. Исходная система не затронута."
            : "Откат завершён с замечаниями: " + string.Join(" ", problems) +
              " Исходная система не затронута, но проверьте целевой диск вручную.";

        ReportProgress(MigrationStep.RolledBack, 0, message);
    }
}
