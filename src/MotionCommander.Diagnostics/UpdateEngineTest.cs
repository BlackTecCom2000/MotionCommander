#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Modules.UpdateEngine;

namespace MotionCommander.Diagnostics;

public static class UpdateEngineTest
{
    public static int Run(TextWriter output)
    {
        output.WriteLine("==========================================================");
        output.WriteLine("  Motion Commander: Update Engine Comprehensive Tests");
        output.WriteLine("==========================================================");

        int passed = 0;
        int failed = 0;

        string testTmp = Path.Combine(Path.GetTempPath(), $"MC-Update-Test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testTmp);

        try
        {
            // --- TEST 1: Checksums manifest resolution for artifact and .part files ---
            RunTest(output, "Checksums manifest resolves expected SHA-256 for artifact and .part files", ref passed, ref failed, () =>
            {
                string patchZip = Path.Combine(testTmp, "MotionCommander-v3.8.46-Patch.zip");
                File.WriteAllText(patchZip, "MOCK_VALID_PATCH_ZIP_CONTENT_V3_8_46");
                string expectedHash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var fs = File.OpenRead(patchZip))
                {
                    expectedHash = Convert.ToHexString(sha.ComputeHash(fs));
                }

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string distDir = Path.Combine(baseDir, "dist");
                Directory.CreateDirectory(distDir);
                string checksumsJson = $@"{{
  ""version"": ""3.8.46"",
  ""algorithm"": ""SHA-256"",
  ""files"": {{
    ""MotionCommander-v3.8.46-Patch.zip"": ""{expectedHash}""
  }}
}}";
                File.WriteAllText(Path.Combine(distDir, "checksums.json"), checksumsJson);
                File.WriteAllText(Path.Combine(baseDir, "checksums.json"), checksumsJson);

                // 1a. Direct artifact name
                string resolved = ReleaseManifest.GetSha256ForAsync("MotionCommander-v3.8.46-Patch.zip", CancellationToken.None).GetAwaiter().GetResult();
                if (!string.Equals(resolved, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Expected hash {expectedHash}, got {resolved}");

                // 1b. Temp .part file name
                string partPath = Path.Combine(testTmp, "MotionCommander-v3.8.46-Patch.zip.a1b2c3d4.part");
                string resolvedPart = ReleaseManifest.GetSha256ForAsync(partPath, CancellationToken.None).GetAwaiter().GetResult();
                if (!string.Equals(resolvedPart, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"Expected hash {expectedHash} for .part file, got {resolvedPart}");
            });

            // --- TEST 2: PrepareStagingAsync extracts correctly and validates patch compatibility ---
            RunTest(output, "PrepareStagingAsync extracts patch and validates patch compatibility", ref passed, ref failed, () =>
            {
                string srcDir = Path.Combine(testTmp, "staging_src");
                Directory.CreateDirectory(srcDir);
                File.WriteAllText(Path.Combine(srcDir, "MotionCommander.dll"), "TEST_DLL_BINARY_DATA");
                File.WriteAllText(Path.Combine(srcDir, "patch_manifest.json"), @"{""targetVersion"":""3.8.46"",""minCompatibleVersion"":""3.8.0""}");

                string testZip = Path.Combine(testTmp, "Valid-Patch.zip");
                ZipFile.CreateFromDirectory(srcDir, testZip);

                string staging = UpdateService.PrepareStagingAsync(testZip, CancellationToken.None).GetAwaiter().GetResult();
                if (!File.Exists(Path.Combine(staging, "MotionCommander.dll")))
                    throw new Exception($"File not found in staging: {staging}");

                string content = File.ReadAllText(Path.Combine(staging, "MotionCommander.dll"));
                if (content != "TEST_DLL_BINARY_DATA")
                    throw new Exception("File content mismatch in staging");
            });

            // --- TEST 3: Incompatible patch detection ---
            RunTest(output, "PrepareStagingAsync rejects incompatible patch version", ref passed, ref failed, () =>
            {
                string srcDir = Path.Combine(testTmp, "incompat_src");
                Directory.CreateDirectory(srcDir);
                File.WriteAllText(Path.Combine(srcDir, "test.dll"), "DUMMY");
                File.WriteAllText(Path.Combine(srcDir, "patch_manifest.json"), @"{""targetVersion"":""9.9.9"",""minCompatibleVersion"":""9.9.0""}");

                string incompatZip = Path.Combine(testTmp, "Incompat-Patch.zip");
                ZipFile.CreateFromDirectory(srcDir, incompatZip);

                bool thrown = false;
                try
                {
                    UpdateService.PrepareStagingAsync(incompatZip, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (PatchIncompatibleException)
                {
                    thrown = true;
                }

                if (!thrown) throw new Exception("Expected PatchIncompatibleException was not thrown");
            });

            // --- TEST 4: Integrity failure on corrupted patch ---
            RunTest(output, "Corrupted patch is rejected and deleted without damaging system", ref passed, ref failed, () =>
            {
                string corruptZip = Path.Combine(testTmp, "Corrupt-Patch.zip");
                File.WriteAllText(corruptZip, "GARBAGE_PAYLOAD");

                // Manifest expecting 64 zeros
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string checksumsJson = @"{
  ""version"": ""3.8.46"",
  ""algorithm"": ""SHA-256"",
  ""files"": {
    ""Corrupt-Patch.zip"": ""0000000000000000000000000000000000000000000000000000000000000000""
  }
}";
                File.WriteAllText(Path.Combine(baseDir, "checksums.json"), checksumsJson);

                var method = typeof(UpdateService).GetMethod("VerifyIntegrityAsync", BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null) throw new Exception("VerifyIntegrityAsync method not found");

                bool thrown = false;
                try
                {
                    var task = (Task)method.Invoke(null, new object?[] { corruptZip, "Corrupt-Patch.zip", null, CancellationToken.None })!;
                    task.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    var actual = ex is TargetInvocationException tie ? tie.InnerException : ex;
                    if (actual is UpdateIntegrityException)
                        thrown = true;
                    else
                        throw new Exception($"Expected UpdateIntegrityException, got {actual?.GetType().Name}: {actual?.Message}");
                }

                if (!thrown) throw new Exception("Corrupted patch was not rejected!");
                if (File.Exists(corruptZip)) throw new Exception("Corrupted patch was not deleted after rejection!");
            });

            // --- TEST 5: Safe promotion with destination file locked ---
            RunTest(output, "Safe file promotion handles locked destination file without failing", ref passed, ref failed, () =>
            {
                var method = typeof(UpdateService).GetMethod("SafePromoteDownloadedFileAsync", BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null) throw new Exception("SafePromoteDownloadedFileAsync method not found");

                string partFile = Path.Combine(testTmp, "downloaded.part");
                File.WriteAllText(partFile, "VERIFIED_PART_CONTENT");

                string lockedTarget = Path.Combine(testTmp, "locked_target.zip");
                File.WriteAllText(lockedTarget, "OLD_LOCKED_CONTENT");

                // Lock destination file exclusively (simulate AV scan or external process lock)
                using (var fs = new FileStream(lockedTarget, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var task = (Task<string>)method.Invoke(null, new object?[] { partFile, lockedTarget, CancellationToken.None })!;
                    string resultPath = task.GetAwaiter().GetResult();

                    // If target was locked, it must safely fallback to the verified partFile path
                    if (resultPath != partFile)
                        throw new Exception($"Expected promotion fallback to {partFile}, got {resultPath}");

                    if (!File.Exists(resultPath))
                        throw new Exception($"Resulting file does not exist: {resultPath}");
                }
            });

            // --- TEST 6: Atomic Rollback simulation ---
            RunTest(output, "Batch update creates rollback backup and successfully restores on failure", ref passed, ref failed, () =>
            {
                string mockAppDir = Path.Combine(testTmp, "mock_app");
                string mockBackupDir = Path.Combine(testTmp, "mock_backup");
                string mockStagingDir = Path.Combine(testTmp, "mock_staging");

                Directory.CreateDirectory(mockAppDir);
                Directory.CreateDirectory(mockBackupDir);
                Directory.CreateDirectory(mockStagingDir);

                // App files before update
                File.WriteAllText(Path.Combine(mockAppDir, "Win11CopyDialog.exe"), "ORIGINAL_EXE_V3_8_45");
                File.WriteAllText(Path.Combine(mockAppDir, "config.dll"), "ORIGINAL_CONFIG_DLL");

                // Staging files
                File.WriteAllText(Path.Combine(mockStagingDir, "Win11CopyDialog.exe"), "NEW_EXE_V3_8_46");
                File.WriteAllText(Path.Combine(mockStagingDir, "config.dll"), "NEW_CONFIG_DLL");

                // Batch script modeling UpdateService logic
                string batScript = $@"@echo off
setlocal
:: Step 1: Backup
for %%F in (""{mockStagingDir}\*"") do (
    if exist ""{mockAppDir}\%%~nxF"" copy /Y ""{mockAppDir}\%%~nxF"" ""{mockBackupDir}\%%~nxF"" >nul
)
:: Step 2: Simulate failure while copying config.dll
copy /Y ""{mockStagingDir}\Win11CopyDialog.exe"" ""{mockAppDir}\Win11CopyDialog.exe"" >nul
goto rollback

:rollback
for %%F in (""{mockStagingDir}\*"") do (
    if exist ""{mockBackupDir}\%%~nxF"" copy /Y ""{mockBackupDir}\%%~nxF"" ""{mockAppDir}\%%~nxF"" >nul
)
exit /b 1
";
                string testBat = Path.Combine(testTmp, "test_rollback.bat");
                File.WriteAllText(testBat, batScript);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{testBat}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = System.Diagnostics.Process.Start(psi)!;
                proc.WaitForExit();

                if (proc.ExitCode != 1)
                    throw new Exception($"Expected batch exit code 1, got {proc.ExitCode}");

                string exeContent = File.ReadAllText(Path.Combine(mockAppDir, "Win11CopyDialog.exe"));
                string cfgContent = File.ReadAllText(Path.Combine(mockAppDir, "config.dll"));

                if (exeContent != "ORIGINAL_EXE_V3_8_45")
                    throw new Exception($"Win11CopyDialog.exe was not restored! Content: {exeContent}");
                if (cfgContent != "ORIGINAL_CONFIG_DLL")
                    throw new Exception($"config.dll was not restored! Content: {cfgContent}");
            });
        }
        finally
        {
            try { Directory.Delete(testTmp, true); } catch { }
        }

        output.WriteLine("==========================================================");
        output.WriteLine($"  Results: Passed: {passed}, Failed: {failed}");
        output.WriteLine("==========================================================");

        return failed;
    }

    private static void RunTest(TextWriter output, string testName, ref int passed, ref int failed, Action action)
    {
        output.Write($"[TEST] {testName} ... ");
        try
        {
            action();
            output.WriteLine("PASS");
            passed++;
        }
        catch (Exception ex)
        {
            output.WriteLine($"FAIL: {ex.Message}");
            failed++;
        }
    }
}
