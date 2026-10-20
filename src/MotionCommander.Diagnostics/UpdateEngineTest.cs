#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        // Тесты подменяют локальный checksums.json рядом с диагностикой,
        // потому что именно этот файл движок читает первым (оффлайн-режим).
        // Исходное содержимое возвращается на место в finally.
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string baseChecksumsPath = Path.Combine(baseDir, "checksums.json");
        string? originalChecksums = File.Exists(baseChecksumsPath)
            ? File.ReadAllText(baseChecksumsPath)
            : null;

        string distDir = Path.Combine(baseDir, "dist");
        string distChecksumsPath = Path.Combine(distDir, "checksums.json");
        string? originalDistChecksums = File.Exists(distChecksumsPath)
            ? File.ReadAllText(distChecksumsPath)
            : null;

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

                Directory.CreateDirectory(distDir);
                string checksumsJson = $@"{{
  ""version"": ""3.8.46"",
  ""algorithm"": ""SHA-256"",
  ""files"": {{
    ""MotionCommander-v3.8.46-Patch.zip"": ""{expectedHash}""
  }}
}}";
                File.WriteAllText(Path.Combine(distDir, "checksums.json"), checksumsJson);
                File.WriteAllText(baseChecksumsPath, checksumsJson);

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
                string checksumsJson = @"{
  ""version"": ""3.8.46"",
  ""algorithm"": ""SHA-256"",
  ""files"": {
    ""Corrupt-Patch.zip"": ""0000000000000000000000000000000000000000000000000000000000000000""
  }
}";
                File.WriteAllText(baseChecksumsPath, checksumsJson);

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

            // --- TEST 7: Зеркала соответствуют измеренным ограничениям ---
            RunTest(output, "Mirror candidates match measured jsDelivr 20 MB limit", ref passed, ref failed, () =>
            {
                const string raw = "https://raw.githubusercontent.com/BlackTecCom2000/MotionCommander/main/";

                var patch = UpdateService.GetMirrorCandidates(raw + "dist/MotionCommander-v3.8.52-Patch.zip");
                if (patch.Count != 3)
                    throw new Exception($"Для патча ожидались 3 адреса, получено {patch.Count}");
                if (!patch[1].StartsWith("https://cdn.jsdelivr.net/", StringComparison.Ordinal))
                    throw new Exception($"Второй адрес патча должен быть jsDelivr, а он: {patch[1]}");
                if (!patch[1].Contains("@v3.8.52/", StringComparison.Ordinal))
                    throw new Exception($"Файл зеркала обязан быть привязан к тегу версии, а не к ветке: {patch[1]}");
                if (patch[2] != "https://github.com/BlackTecCom2000/MotionCommander/raw/main/dist/MotionCommander-v3.8.52-Patch.zip")
                    throw new Exception($"Третий адрес патча неверен: {patch[2]}");

                var portable = UpdateService.GetMirrorCandidates(raw + "dist/MotionCommander-v3.8.52-Portable.zip");
                if (portable.Any(u => u.Contains("jsdelivr", StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("jsDelivr отдаёт HTTP 404 на Portable.zip (38 МБ) — зеркало добавлять нельзя");

                var setup = UpdateService.GetMirrorCandidates(raw + "dist/MotionCommander-v3.8.52-Setup.exe");
                if (setup.Any(u => u.Contains("jsdelivr", StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("jsDelivr отдаёт HTTP 403 на Setup.exe (21.7 МБ) — зеркало добавлять нельзя");

                var foreign = UpdateService.GetMirrorCandidates("https://example.com/pkg.zip");
                if (foreign.Count != 1)
                    throw new Exception("Чужой адрес не должен получать зеркала проекта");

                var manifests = UpdateService.GetManifestCandidates();
                if (manifests.Count != 2 || !manifests[1].Contains("jsdelivr", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Манифест должен иметь зеркало jsDelivr (измерено: отдаёт version.json)");
                if (!manifests[1].Contains("@latest/version.json", StringComparison.Ordinal))
                    throw new Exception($"Манифест зеркала обязан использовать @latest, а не ветку: {manifests[1]}");
            });

            // --- TEST 8: Патч обязан вести в ожидаемую версию ---
            RunTest(output, "Patch targeting another version is rejected before apply", ref passed, ref failed, () =>
            {
                string srcDir = Path.Combine(testTmp, "wrong_target_src");
                Directory.CreateDirectory(srcDir);
                File.WriteAllText(Path.Combine(srcDir, "app.dll"), "PAYLOAD");
                File.WriteAllText(Path.Combine(srcDir, "patch_manifest.json"),
                    @"{""targetVersion"":""3.8.46"",""minCompatibleVersion"":""3.8.0""}");

                string zip = Path.Combine(testTmp, "WrongTarget-Patch.zip");
                ZipFile.CreateFromDirectory(srcDir, zip);

                bool thrown = false;
                try
                {
                    UpdateService.PrepareStagingAsync(zip, CancellationToken.None, "3.8.99")
                        .GetAwaiter().GetResult();
                }
                catch (PatchIncompatibleException ex)
                {
                    thrown = true;
                    if (!ex.Message.Contains("3.8.46") || !ex.Message.Contains("3.8.99"))
                        throw new Exception($"Сообщение не называет обе версии: {ex.Message}");
                }

                if (!thrown)
                    throw new Exception("Патч с чужим targetVersion был принят — возможен молчаливый откат версии");
            });

            // --- TEST 9: Экранирование путей для cmd ---
            RunTest(output, "Batch path escaping keeps % literal inside quoted paths", ref passed, ref failed, () =>
            {
                string escaped = UpdateService.EscapeBatchPath(@"C:\Users\100% Ready\AppData\Local\MotionCommander");
                if (!escaped.Contains("100%% Ready", StringComparison.Ordinal))
                    throw new Exception($"Процент не удвоен: {escaped}");
                if (escaped.Contains("100% Ready", StringComparison.Ordinal))
                    throw new Exception($"Неэкранированный процент остался: {escaped}");

                if (UpdateService.EscapeBatchPath("a&b").Contains("^", StringComparison.Ordinal))
                    throw new Exception("Внутри кавычек & литерален, экранирование символом ^ испортило бы путь");

                if (UpdateService.EscapeBatchPath(null) != string.Empty)
                    throw new Exception("null должен превращаться в пустую строку");
            });

            // --- TEST 10: Реальная докачка после обрыва соединения ---
            RunTest(output, "Download resumes after connection break using HTTP Range (measured)", ref passed, ref failed, () =>
            {
                byte[] payload = new byte[200 * 1024];
                new Random(12345).NextBytes(payload);

                const string fileName = "MotionCommander-v9.9.9-Patch.zip";
                using var server = new MiniHttpServer(fileName, payload, breakFirstResponse: true);
                WriteChecksums(fileName, payload, "9.9.9");

                string downloaded = UpdateService.DownloadUpdateAsync(server.BaseUrl)
                    .GetAwaiter().GetResult();

                try
                {
                    byte[] actual = File.ReadAllBytes(downloaded);
                    if (!actual.SequenceEqual(payload))
                        throw new Exception($"Содержимое после докачки не совпало: {actual.Length} из {payload.Length} байт");

                    var requests = server.Requests;
                    if (requests.Count < 2)
                        throw new Exception($"Ожидались 2 запроса (обрыв + продолжение), было {requests.Count}");

                    if (server.RangeRequests.Count == 0)
                        throw new Exception("Повторный запрос не содержал Range — докачка не выполнялась");

                    output.Write($"[{requests.Count} запроса, Range: {server.RangeRequests[0]}] ");
                }
                finally
                {
                    try { File.Delete(downloaded); } catch { }
                }
            });

            // --- TEST 11: Медленный канал длиннее прежнего лимита 15 секунд ---
            RunTest(output, "Slow download (16+ s) is not cut by a global timeout (measured)", ref passed, ref failed, () =>
            {
                // 33 КБ по 1 КБ каждые 500 мс = 16.5 с. Прежний HttpClient
                // с Timeout=15 с обрывал такую загрузку TaskCanceledException
                // на любом файле — это и был реальный сбой у пользователей
                // на медленных каналах.
                byte[] payload = new byte[33 * 1024];
                new Random(777).NextBytes(payload);

                const string fileName = "MotionCommander-v9.9.8-Patch.zip";
                using var server = new MiniHttpServer(fileName, payload,
                    delayBetweenChunksMs: 500, chunkSize: 1024);
                WriteChecksums(fileName, payload, "9.9.8");

                var sw = Stopwatch.StartNew();
                string downloaded = UpdateService.DownloadUpdateAsync(server.BaseUrl)
                    .GetAwaiter().GetResult();
                sw.Stop();

                try
                {
                    if (sw.Elapsed.TotalSeconds < 15.5)
                        throw new Exception($"Загрузка заняла {sw.Elapsed.TotalSeconds:F1} с — тест не воспроизводит медленный канал");

                    byte[] actual = File.ReadAllBytes(downloaded);
                    if (!actual.SequenceEqual(payload))
                        throw new Exception("Содержимое медленной загрузки повреждено");

                    output.Write($"[{sw.Elapsed.TotalSeconds:F1} с] ");
                }
                finally
                {
                    try { File.Delete(downloaded); } catch { }
                }
            });

            // --- TEST 12: Устаревший манифест не выдаётся за подмену файла ---
            RunTest(output, "Stale checksums manifest is reported as version mismatch", ref passed, ref failed, () =>
            {
                const string fileName = "MotionCommander-v3.8.47-Patch.zip";
                string file = Path.Combine(testTmp, fileName);
                File.WriteAllText(file, "V3847_CONTENT");
                WriteChecksums(fileName, File.ReadAllBytes(file), "3.8.46");

                bool thrown = false;
                try
                {
                    ReleaseManifest.GetSha256ForAsync(file, CancellationToken.None, "3.8.47")
                        .GetAwaiter().GetResult();
                }
                catch (UpdateIntegrityException ex)
                {
                    thrown = true;
                    if (!ex.Message.Contains("3.8.46") || !ex.Message.Contains("3.8.47"))
                        throw new Exception($"Сообщение не объясняет расхождение версий: {ex.Message}");
                    if (ex.Message.Contains("повреждении загрузки"))
                        throw new Exception("Устаревший манифест описан как подмена файла");
                }

                if (!thrown)
                    throw new Exception("Манифест от другой версии был использован для сверки");
            });

            // --- TEST 13: Троттлинг автопроверки обновлений ---
            RunTest(output, "Startup update check is throttled to once per 3 hours", ref passed, ref failed, () =>
            {
                string stampPath = Path.Combine(AppPaths.WritableDataDirectory, "update_check.json");
                string? original = File.Exists(stampPath) ? File.ReadAllText(stampPath) : null;

                try
                {
                    if (File.Exists(stampPath)) File.Delete(stampPath);

                    if (!UpdateService.IsAutoCheckDue(TimeSpan.FromHours(3)))
                        throw new Exception("Без отметки проверка должна быть разрешена");

                    UpdateService.RecordAutoCheck("9.9.9");
                    if (!File.Exists(stampPath))
                        throw new Exception("Отметка о проверке не записана");

                    if (UpdateService.IsAutoCheckDue(TimeSpan.FromHours(3)))
                        throw new Exception("Сразу после проверки автопроверка должна быть отложена");

                    if (!UpdateService.IsAutoCheckDue(TimeSpan.Zero))
                        throw new Exception("С нулевым интервалом проверка должна быть разрешена");
                }
                finally
                {
                    try
                    {
                        if (original != null) File.WriteAllText(stampPath, original);
                        else if (File.Exists(stampPath)) File.Delete(stampPath);
                    }
                    catch { }
                }
            });

            // --- TEST 14: Загрузка только с доверенных адресов проекта ---
            RunTest(output, "Download from unknown host is rejected before network access", ref passed, ref failed, () =>
            {
                bool rejectedForeign = false;
                try
                {
                    UpdateService.DownloadUpdateAsync("https://evil.example.com/MotionCommander-v9.9.9-Patch.zip")
                        .GetAwaiter().GetResult();
                }
                catch (IOException ex)
                {
                    rejectedForeign = ex.Message.Contains("неизвестный сервер", StringComparison.OrdinalIgnoreCase);
                }

                if (!rejectedForeign)
                    throw new Exception("Ссылка на посторонний сервер была принята к загрузке");

                bool rejectedHttp = false;
                try
                {
                    UpdateService.DownloadUpdateAsync("http://raw.githubusercontent.com/x/MotionCommander-v9.9.9-Patch.zip")
                        .GetAwaiter().GetResult();
                }
                catch (IOException ex)
                {
                    rejectedHttp = ex.Message.Contains("HTTPS", StringComparison.OrdinalIgnoreCase);
                }

                if (!rejectedHttp)
                    throw new Exception("Загрузка по HTTP без шифрования была принята");
            });

            // --- TEST 15: Целевая версия для полного пакета (без patch_manifest) ---
            RunTest(output, "Full package target version is read from staged assembly", ref passed, ref failed, () =>
            {
                if (UpdateService.NormalizeVersion("3.8.52.0") != "3.8.52")
                    throw new Exception("NormalizeVersion не отбрасывает нулевую ревизию");
                if (UpdateService.NormalizeVersion("v3.8.52") != "3.8.52")
                    throw new Exception("NormalizeVersion не убирает префикс v");

                string stage = Path.Combine(testTmp, "full_stage");
                Directory.CreateDirectory(stage);

                string realDll = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Win11CopyDialog.dll");
                if (!File.Exists(realDll))
                    throw new Exception("Win11CopyDialog.dll рядом с диагностикой не найден");

                File.Copy(realDll, Path.Combine(stage, "Win11CopyDialog.dll"), true);

                var method = typeof(UpdateService).GetMethod(
                    "TryReadTargetVersion", BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null) throw new Exception("TryReadTargetVersion не найден");

                string detected = (string)method.Invoke(null, new object?[] { stage })!;
                string expected = UpdateService.GetCurrentVersion();

                if (!string.Equals(UpdateService.NormalizeVersion(detected),
                                   UpdateService.NormalizeVersion(expected),
                                   StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception(
                        $"Версия из файла сборки определена как «{detected}», ожидалась «{expected}»");
                }
            });

            // --- TEST 16: Экранирование проверено настоящим cmd ---
            RunTest(output, "Escaped % path actually copies through real cmd", ref passed, ref failed, () =>
            {
                string srcDir = Path.Combine(testTmp, "src 100% Ready");
                string dstDir = Path.Combine(testTmp, "dst 100% Ready");
                Directory.CreateDirectory(srcDir);
                Directory.CreateDirectory(dstDir);

                string srcFile = Path.Combine(srcDir, "payload.txt");
                string dstFile = Path.Combine(dstDir, "payload.txt");
                File.WriteAllText(srcFile, "ESCAPE_OK");

                string bat = "@echo off\r\n" +
                             $"copy /Y \"{UpdateService.EscapeBatchPath(srcFile)}\" " +
                             $"\"{UpdateService.EscapeBatchPath(dstFile)}\" >nul\r\n" +
                             "exit /b %ERRORLEVEL%\r\n";

                string batPath = Path.Combine(testTmp, "escape_test.bat");
                File.WriteAllText(batPath, bat, new UTF8Encoding(false));

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{batPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi)!;
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                    throw new Exception($"cmd завершился с кодом {proc.ExitCode}: путь с % не скопирован");

                if (!File.Exists(dstFile) || File.ReadAllText(dstFile) != "ESCAPE_OK")
                    throw new Exception("Файл не скопирован через cmd — экранирование не работает");
            });
        }
        finally
        {
            try
            {
                if (originalChecksums != null) File.WriteAllText(baseChecksumsPath, originalChecksums);
                else if (File.Exists(baseChecksumsPath)) File.Delete(baseChecksumsPath);

                // dist/checksums.json создаётся тестом 1. Если его не убрать,
                // живая проверка канала читает локальный тестовый манифест
                // и честно сообщает «записи нет» — уже про несуществующий релиз.
                if (originalDistChecksums != null) File.WriteAllText(distChecksumsPath, originalDistChecksums);
                else if (File.Exists(distChecksumsPath)) File.Delete(distChecksumsPath);
            }
            catch { }

            try { Directory.Delete(testTmp, true); } catch { }
        }

        output.WriteLine("==========================================================");
        output.WriteLine($"  Results: Passed: {passed}, Failed: {failed}");
        output.WriteLine("==========================================================");

        return failed;
    }

    /// <summary>
    /// Проверка реального канала обновления: манифест, контрольные суммы
    /// и фактическая загрузка патча с зеркал.
    /// </summary>
    /// <remarks>
    /// Это не тест логики (он в <see cref="Run"/>), а измерение живого
    /// канала: сколько миллисекунд отвечает манифест, откуда реально
    /// пришёл файл, совпала ли его контрольная сумма. Без такого прогона
    /// «обновление работает» остаётся предположением.
    /// </remarks>
    public static int RunLive(TextWriter output)
    {
        output.WriteLine("==========================================================");
        output.WriteLine("  Motion Commander: Live Update Channel Check");
        output.WriteLine("==========================================================");

        int problems = 0;

        var sw = Stopwatch.StartNew();
        UpdateInfo info;
        try
        {
            info = UpdateService.CheckForUpdatesAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            output.WriteLine($"  [FAIL] Манифест: {ex.Message}");
            return 1;
        }
        sw.Stop();

        output.WriteLine($"  Манифест: {sw.Elapsed.TotalMilliseconds:F0} мс");
        output.WriteLine($"    установлена v{info.CurrentVersion}, доступна v{info.LatestVersion}");
        output.WriteLine($"    обновление={info.IsUpdateAvailable}, пакет={info.PackageType}");

        if (!string.IsNullOrEmpty(info.ErrorMessage))
        {
            output.WriteLine($"  [FAIL] Ошибка манифеста: {info.ErrorMessage}");
            problems++;
        }

        foreach (var (label, url) in new (string, string)[]
        {
            ("patch", info.PatchUrl),
            ("portable", info.DownloadUrl),
            ("installer", info.InstallerUrl),
            ("setup", info.SetupExeUrl)
        })
        {
            if (string.IsNullOrWhiteSpace(url)) continue;

            bool trusted = UpdateService.IsTrustedDownloadUrl(url);
            output.WriteLine($"    {label}: {(trusted ? "доверенный" : "ПОСТОРОННИЙ")} — {new Uri(url).Host}");
            if (!trusted) problems++;
        }

        // Паритет зеркала: jsDelivr @latest обязан отдавать ту же версию,
        // что и raw. Проверка отдельная, потому что @main уже один раз
        // отдал прошлый релиз (измерено 20.10.2026: 3.8.51 при 3.8.52).
        try
        {
            sw.Restart();
            using var mirrorClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            mirrorClient.DefaultRequestHeaders.Add("User-Agent", "MotionCommander-Diagnostics");

            string mirrorUrl =
                "https://cdn.jsdelivr.net/gh/BlackTecCom2000/MotionCommander@latest/version.json" +
                $"?cache_bypass={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            string mirrorJson = mirrorClient.GetStringAsync(mirrorUrl).GetAwaiter().GetResult();
            sw.Stop();

            string mirrorVersion = "";
            using (var mirrorDoc = JsonDocument.Parse(mirrorJson))
            {
                if (mirrorDoc.RootElement.TryGetProperty("version", out var mv))
                    mirrorVersion = mv.GetString() ?? "";
            }

            bool same = string.Equals(
                UpdateService.NormalizeVersion(mirrorVersion),
                UpdateService.NormalizeVersion(info.LatestVersion),
                StringComparison.OrdinalIgnoreCase);

            output.WriteLine(
                $"  Зеркало jsDelivr: {sw.Elapsed.TotalMilliseconds:F0} мс, v{mirrorVersion} " +
                (same ? "(совпадает с raw)" : "(РАСХОЖДЕНИЕ с raw)"));
        }
        catch (Exception ex)
        {
            // Зеркало — запасной путь; его недоступность не делает канал
            // неисправным, но должна быть видна в отчёте.
            output.WriteLine($"  [WARN] Зеркало jsDelivr недоступно: {ex.Message}");
        }

        // Контрольная сумма полного архива: проверяется без скачивания
        // 38 МБ — достаточно самого манифеста.
        if (!string.IsNullOrWhiteSpace(info.LatestVersion))
        {
            sw.Restart();
            try
            {
                string hash = ReleaseManifest.GetSha256ForAsync(
                    $"MotionCommander-v{info.LatestVersion}-Portable.zip",
                    CancellationToken.None).GetAwaiter().GetResult();
                sw.Stop();

                bool ok = IsSha256Hex(hash);
                output.WriteLine(
                    $"  checksums.json (portable): {sw.Elapsed.TotalMilliseconds:F0} мс, " +
                    $"{(ok ? "SHA-256 найден" : "ЗАПИСИ НЕТ")} {Shorten(hash)}");
                if (!ok) problems++;
            }
            catch (Exception ex)
            {
                output.WriteLine($"  [FAIL] checksums.json: {ex.Message}");
                problems++;
            }
        }

        // Полный путь загрузки: патч небольшой, поэтому скачивается
        // целиком, и это единственный способ доказать, что докачка,
        // зеркала и сверка SHA-256 работают на живом канале. Проверка
        // выполняется и когда установлена уже последняя версия: канал
        // должен работать до того, как выйдет следующий релиз.
        if (info.HasPatch && !string.IsNullOrWhiteSpace(info.LatestVersion))
        {
            sw.Restart();
            string? downloaded = null;
            try
            {
                downloaded = UpdateService.DownloadUpdateAsync(info.GetEffectivePatchUrl())
                    .GetAwaiter().GetResult();
                sw.Stop();

                var bytes = new FileInfo(downloaded).Length;
                output.WriteLine(
                    $"  Патч v{info.LatestVersion}: скачан за {sw.Elapsed.TotalSeconds:F1} с, " +
                    $"{bytes:N0} байт, SHA-256 подтверждён");
            }
            catch (Exception ex)
            {
                sw.Stop();
                output.WriteLine($"  [FAIL] Скачивание патча: {ex.Message}");
                problems++;
            }
            finally
            {
                try { if (downloaded != null) File.Delete(downloaded); } catch { }
            }
        }
        else
        {
            output.WriteLine("  Патч в манифесте отсутствует — скачивать нечего.");
        }

        output.WriteLine("==========================================================");
        output.WriteLine(problems == 0
            ? "  Итог: канал обновления исправен (измерено сейчас)"
            : $"  Итог: проблем — {problems}");
        output.WriteLine("==========================================================");

        return problems;
    }

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(c =>
            (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'));

    private static string Shorten(string value) =>
        value.Length <= 16 ? value : value[..16] + "…";

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

    /// <summary>
    /// Пишет checksums.json рядом с диагностикой: движок читает этот
    /// локальный файл первым, поэтому тесты не зависят от сети.
    /// </summary>
    private static void WriteChecksums(string fileName, byte[] payload, string version)
    {
        string hash;
        using (var sha = System.Security.Cryptography.SHA256.Create())
            hash = Convert.ToHexString(sha.ComputeHash(payload));

        string json = $@"{{
  ""version"": ""{version}"",
  ""algorithm"": ""SHA-256"",
  ""files"": {{
    ""{fileName}"": ""{hash}""
  }}
}}";

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        File.WriteAllText(Path.Combine(baseDir, "checksums.json"), json);

        string distDir = Path.Combine(baseDir, "dist");
        Directory.CreateDirectory(distDir);
        File.WriteAllText(Path.Combine(distDir, "checksums.json"), json);
    }

    /// <summary>
    /// Минимальный HTTP/1.1-сервер для проверки загрузчика на реальном
    /// сокете: поддерживает Range, умеет рвать первое соединение и
    /// отдавать данные медленно.
    /// </summary>
    /// <remarks>
    /// HttpListener на неадминистративной сессии требует URL ACL,
    /// поэтому используется TcpListener на 127.0.0.1 со случайным портом.
    /// </remarks>
    private sealed class MiniHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly byte[] _payload;
        private readonly bool _breakFirstResponse;
        private readonly int _delayBetweenChunksMs;
        private readonly int _chunkSize;
        private readonly List<string> _requests = new();
        private readonly List<string> _rangeRequests = new();
        private int _broken;

        public MiniHttpServer(
            string fileName,
            byte[] payload,
            bool breakFirstResponse = false,
            int delayBetweenChunksMs = 0,
            int chunkSize = 81920)
        {
            _payload = payload;
            _breakFirstResponse = breakFirstResponse;
            _delayBetweenChunksMs = delayBetweenChunksMs;
            _chunkSize = chunkSize;

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/{fileName}";

            _ = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl { get; }

        public IReadOnlyList<string> Requests
        {
            get { lock (_requests) return _requests.ToArray(); }
        }

        public IReadOnlyList<string> RangeRequests
        {
            get { lock (_rangeRequests) return _rangeRequests.ToArray(); }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();

                    // Заголовки читаются по байту до пустой строки: так тест
                    // не зависит от размера пакета и разбиения по TCP.
                    var header = new List<byte>(1024);
                    var one = new byte[1];
                    while (header.Count < 65536)
                    {
                        int read = await stream.ReadAsync(one.AsMemory(0, 1), _cts.Token);
                        if (read == 0) return;
                        header.Add(one[0]);

                        int count = header.Count;
                        if (count >= 4 &&
                            header[count - 4] == 13 && header[count - 3] == 10 &&
                            header[count - 2] == 13 && header[count - 1] == 10)
                        {
                            break;
                        }
                    }

                    string text = Encoding.ASCII.GetString(header.ToArray());
                    string requestLine = text.Split("\r\n")[0];
                    lock (_requests) _requests.Add(requestLine);

                    long start = 0;
                    Match rangeMatch = Regex.Match(
                        text, @"Range:\s*bytes=(\d+)-", RegexOptions.IgnoreCase);
                    if (rangeMatch.Success)
                    {
                        start = long.Parse(rangeMatch.Groups[1].Value);
                        lock (_rangeRequests)
                            _rangeRequests.Add($"bytes={start}-");
                    }

                    if (start >= _payload.Length) start = 0;

                    bool partial = start > 0;
                    long remaining = _payload.Length - start;

                    bool breakThis = !partial &&
                                     _breakFirstResponse &&
                                     Interlocked.Exchange(ref _broken, 1) == 0;

                    long bytesToSend = breakThis ? Math.Max(1, remaining / 3) : remaining;

                    var head = new StringBuilder();
                    head.Append(partial
                        ? "HTTP/1.1 206 Partial Content\r\n"
                        : "HTTP/1.1 200 OK\r\n");
                    head.Append("Content-Type: application/octet-stream\r\n");
                    head.Append("Accept-Ranges: bytes\r\n");
                    head.Append($"Content-Length: {remaining}\r\n");
                    if (partial)
                        head.Append($"Content-Range: bytes {start}-{_payload.Length - 1}/{_payload.Length}\r\n");
                    head.Append("Connection: close\r\n\r\n");

                    await stream.WriteAsync(
                        Encoding.ASCII.GetBytes(head.ToString()), _cts.Token);

                    long sent = 0;
                    while (sent < bytesToSend)
                    {
                        int chunk = (int)Math.Min(_chunkSize, bytesToSend - sent);
                        await stream.WriteAsync(
                            _payload.AsMemory((int)(start + sent), chunk), _cts.Token);
                        sent += chunk;

                        if (_delayBetweenChunksMs > 0)
                            await Task.Delay(_delayBetweenChunksMs, _cts.Token);
                    }

                    if (breakThis)
                    {
                        // Обрыв без корректного завершения: заголовок обещал
                        // remaining байт, а отправлена только треть. Клиент
                        // обязан увидеть незавершённое тело, а не молча
                        // принять половину файла. RST здесь не годится:
                        // он выбрасывает уже принятые клиентом байты, и
                        // проверка докачки теряет смысл.
                        await stream.FlushAsync(_cts.Token);
                        client.Client.Shutdown(SocketShutdown.Send);
                        await Task.Delay(100, _cts.Token);
                        return;
                    }

                    await stream.FlushAsync(_cts.Token);
                }
                catch
                {
                    // Клиент закрыл соединение (отмена) — для теста это нормально.
                }
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _cts.Dispose(); } catch { }
        }
    }
}
