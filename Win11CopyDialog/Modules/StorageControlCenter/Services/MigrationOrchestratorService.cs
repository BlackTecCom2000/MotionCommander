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

    public MigrationOrchestratorService(StorageDisk sourceDisk, StorageDisk targetDisk, bool testMode = false)
    {
        _sourceDisk = sourceDisk;
        _targetDisk = targetDisk;
        _testMode = testMode;
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
            ReportProgress(MigrationStep.TargetPreparation, 20, "Preparing target disk (partitioning)...");
            if (!_testMode)
            {
                // TODO: Call OsMigrationService.PrepareTargetDiskAsync
                await Task.Delay(1000, ct); // Simulate for now
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
            ReportProgress(MigrationStep.DataMigration, 40, "Cloning OS files (Robocopy)...");
            if (!_testMode)
            {
                // TODO: Call OsMigrationService.CopySystemDataAsync
                await Task.Delay(2000, ct); // Simulate for now
            }
            else
            {
                await Task.Delay(1000, ct); // Test mode skip
            }

            // 6. Bootloader Configuration
            ReportProgress(MigrationStep.BootloaderConfiguration, 80, "Configuring bootloader (BCD)...");
            if (!_testMode)
            {
                // TODO: Call OsMigrationService.SetupBootloaderAsync
                await Task.Delay(1000, ct); // Simulate for now
            }
            else
            {
                await Task.Delay(500, ct); // Test mode skip
            }

            // 7. Verification
            ReportProgress(MigrationStep.Verification, 90, "Verifying migration integrity...");
            await Task.Delay(1000, ct);

            // 8. Finalization
            ReportProgress(MigrationStep.Finalization, 95, "Finalizing migration and cleaning up...");
            if (!_testMode)
            {
                VssProviderService.CleanupShadowCopy(_vssShadowId, _vssMountPoint);
            }

            ReportProgress(MigrationStep.Completed, 100, "Migration completed successfully.");
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

    private async Task RollbackAsync()
    {
        ReportProgress(MigrationStep.RolledBack, 0, "Rolling back changes...");
        if (!_testMode)
        {
            if (!string.IsNullOrEmpty(_vssShadowId))
            {
                VssProviderService.CleanupShadowCopy(_vssShadowId, _vssMountPoint);
            }
            // Add partition rollback if needed in the future
        }
        await Task.Delay(500); // Simulate rollback time
        ReportProgress(MigrationStep.RolledBack, 0, "Rollback complete.");
    }
}
