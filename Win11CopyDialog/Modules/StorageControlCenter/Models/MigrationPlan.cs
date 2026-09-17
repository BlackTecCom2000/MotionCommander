using System.Collections.Generic;

namespace Win11CopyDialog.Modules.StorageControlCenter.Models;

public enum MigrationMode
{
    SafeMigration,  // Non-destructive, shrinks partitions or uses unallocated space
    FullDiskClone   // Destructive, cleans the entire disk before migrating
}

public class MigrationPlan
{
    public MigrationMode Mode { get; set; } = MigrationMode.SafeMigration;
    
    public int TargetDiskNumber { get; set; }
    
    public string TargetDiskModel { get; set; } = string.Empty;

    public bool IsDestructive { get; set; }
    
    /// <summary>
    /// If IsDestructive is true, the user MUST confirm by typing the disk model or explicitly accepting the risk.
    /// </summary>
    public bool UserConfirmedOverride { get; set; }
    
    public long RequiredSpaceMB { get; set; }
    
    public long AvailableUnallocatedSpaceMB { get; set; }
    
    /// <summary>
    /// A human-readable list of steps that will be performed. Useful for UI wizards.
    /// </summary>
    public List<string> PlannedSteps { get; set; } = new();

    /// <summary>
    /// Will contain warnings about data loss or space constraints.
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    public bool IsValid => Warnings.Count == 0 && (IsDestructive ? UserConfirmedOverride : true);
}
