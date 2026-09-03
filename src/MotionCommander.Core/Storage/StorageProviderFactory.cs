using MotionCommander.Core.Common;
using MotionCommander.Core.Storage.Linux;
using MotionCommander.Core.Storage.Mac;
using MotionCommander.Core.Storage.Windows;

namespace MotionCommander.Core.Storage;

public static class StorageProviderFactory
{
    private static IStorageProvider? _instance;

    public static IStorageProvider Default
    {
        get
        {
            if (_instance != null) return _instance;

            _instance = PlatformDetector.CurrentPlatform switch
            {
                TargetPlatform.Linux => new LinuxStorageProvider(),
                TargetPlatform.MacOS => new MacStorageProvider(),
                TargetPlatform.Windows => new WindowsStorageProvider(),
                _ => new WindowsStorageProvider()
            };

            return _instance;
        }
    }
}
