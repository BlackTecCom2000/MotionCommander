using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Win11CopyDialog.Modules.StorageControlCenter.Models;

namespace Win11CopyDialog.Modules.StorageControlCenter.Services;

public enum PreflightIssueSeverity
{
    Info,
    Warning,
    Blocker
}

public class PreflightIssue
{
    public PreflightIssueSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class MigrationPreflightResult
{
    public bool IsSafeToProceed => !Issues.Any(i => i.Severity == PreflightIssueSeverity.Blocker);
    public List<PreflightIssue> Issues { get; set; } = new();
    public bool CanShrinkExistingPartitions { get; set; }
    public long RequiredSpaceBytes { get; set; }
    public long AvailableSpaceBytes { get; set; }
    public List<StoragePartition> ProtectedPartitions { get; set; } = new();
}

public static class MigrationPreflightService
{
    /// <summary>
    /// Analyzes the source and target disks to ensure a safe migration.
    /// This follows the "Never perform a destructive operation without reason" principle.
    /// </summary>
    public static async Task<MigrationPreflightResult> RunPreflightCheckAsync(StorageDisk sourceDisk, StorageDisk targetDisk, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var result = new MigrationPreflightResult();

            // 1. Ensure source and target are valid and not the same
            if (sourceDisk == null || targetDisk == null)
            {
                result.Issues.Add(new PreflightIssue { Severity = PreflightIssueSeverity.Blocker, Message = "Source or target disk is null." });
                return result;
            }

            if (sourceDisk.DiskNumber == targetDisk.DiskNumber)
            {
                result.Issues.Add(new PreflightIssue { Severity = PreflightIssueSeverity.Blocker, Message = "Source and target disks cannot be the same." });
                return result;
            }

            // 2. Identify required space from source OS partition(s)
            long requiredSpace = 0;
            foreach (var partition in sourceDisk.Partitions)
            {
                if (partition.IsSystem || partition.IsBoot || partition.DriveLetter.Equals("C", StringComparison.OrdinalIgnoreCase))
                {
                    requiredSpace += partition.UsedSpaceBytes;
                }
            }

            // Add a 10% safety buffer for VSS and OS expansion
            requiredSpace = (long)(requiredSpace * 1.10);
            result.RequiredSpaceBytes = requiredSpace;

            // 3. Analyze Target Disk Capacity
            result.AvailableSpaceBytes = targetDisk.TotalSizeBytes;
            if (result.AvailableSpaceBytes < result.RequiredSpaceBytes)
            {
                result.Issues.Add(new PreflightIssue 
                { 
                    Severity = PreflightIssueSeverity.Blocker, 
                    Message = $"Target disk capacity ({Win11CopyDialog.Helpers.Formatters.Bytes(result.AvailableSpaceBytes)}) is smaller than the required space ({Win11CopyDialog.Helpers.Formatters.Bytes(result.RequiredSpaceBytes)})." 
                });
            }

            // 4. Identify Protected Partitions on Target
            foreach (var partition in targetDisk.Partitions)
            {
                if (!partition.IsAllocated) continue;

                // Rule: System, Boot, MSR, Recovery, and UNKNOWN are protected.
                if (partition.IsSystem || 
                    partition.IsBoot || 
                    partition.Category == PartitionTypeCategory.SystemEfi ||
                    partition.Category == PartitionTypeCategory.MicrosoftReserved ||
                    partition.Category == PartitionTypeCategory.Recovery ||
                    partition.Category == PartitionTypeCategory.Unknown)
                {
                    result.ProtectedPartitions.Add(partition);
                }
                // Rule: If it has data and it's not explicitly confirmed, treat it as warning/protected
                else if (partition.UsedSpaceBytes > 100 * 1024 * 1024) // > 100MB used
                {
                    result.ProtectedPartitions.Add(partition);
                }
            }

            // 5. Evaluate Shrink vs Wipe
            if (targetDisk.Partitions.Count > 0)
            {
                if (result.ProtectedPartitions.Count > 0)
                {
                    result.Issues.Add(new PreflightIssue
                    {
                        Severity = PreflightIssueSeverity.Blocker,
                        Message = $"Target disk contains {result.ProtectedPartitions.Count} protected partition(s). A full wipe is blocked. A safe resize/shrink plan must be used."
                    });
                    
                    // Check if we can shrink a non-protected partition to fit
                    long maxShrinkableFreeSpace = targetDisk.Partitions
                        .Where(p => !result.ProtectedPartitions.Contains(p) && p.Category == PartitionTypeCategory.BasicData)
                        .Sum(p => p.FreeSpaceBytes);
                        
                    if (maxShrinkableFreeSpace >= result.RequiredSpaceBytes)
                    {
                        result.CanShrinkExistingPartitions = true;
                        result.Issues.Add(new PreflightIssue
                        {
                            Severity = PreflightIssueSeverity.Info,
                            Message = "Sufficient free space exists within existing partitions to safely shrink and create the OS clone without wiping the disk."
                        });
                    }
                    else
                    {
                        result.Issues.Add(new PreflightIssue
                        {
                            Severity = PreflightIssueSeverity.Blocker,
                            Message = "Not enough free space to shrink existing partitions safely."
                        });
                    }
                }
                else
                {
                    result.Issues.Add(new PreflightIssue
                    {
                        Severity = PreflightIssueSeverity.Warning,
                        Message = "Target disk contains existing partitions. A full wipe will destroy this data."
                    });
                }
            }
            else
            {
                result.Issues.Add(new PreflightIssue
                {
                    Severity = PreflightIssueSeverity.Info,
                    Message = "Target disk is empty and ready for a full clone."
                });
            }

            return result;
        }, ct);
    }
}
