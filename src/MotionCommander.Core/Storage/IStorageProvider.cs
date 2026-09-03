using MotionCommander.Core.Models;

namespace MotionCommander.Core.Storage;

public interface IStorageProvider
{
    Task<List<StorageDiskInfo>> GetPhysicalDisksAsync();
    Task<List<PartitionInfo>> GetPartitionsAsync(int diskIndex);
    Task<SmartReport> GetSmartReportAsync(int diskIndex);
    Task<bool> OptimizeDiskAsync(int diskIndex, IProgress<string>? progress = null);
    Task<bool> FormatPartitionAsync(string devicePathOrLetter, string fileSystem, string label, bool quick);
    Task<bool> DeletePartitionAsync(int diskIndex, int partitionNumber, bool overrideLocks);
}
