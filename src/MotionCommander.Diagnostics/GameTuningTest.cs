#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Win11CopyDialog.Modules.GameTuning;

namespace MotionCommander.Diagnostics;

/// <summary>
/// Проверки системы оптимизации игр. Ни одна проверка не меняет настоящие
/// настройки пользователя: реестр — только во временной ветви, конфиги —
/// только во временных файлах. Замер FPS без прав администратора проверяется
/// по честному отказу, а не по подставленным числам.
/// </summary>
public static class GameTuningTest
{
    public static int Run(TextWriter output)
    {
        output.WriteLine("==========================================================");
        output.WriteLine("  Motion Commander: Game Tuning & FPS Measurement Tests");
        output.WriteLine("==========================================================");

        int passed = 0;
        int failed = 0;
        string testTmp = Path.Combine(Path.GetTempPath(), "MC-GameTuning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testTmp);

        try
        {
            RunTest(output, "FPS: стабильные 60 кадров дают 60 FPS и нулевые фризы", ref passed, ref failed, () =>
            {
                var frames = Enumerable.Repeat(1000.0 / 60.0, 120).ToList();
                FpsResult result = FpsStats.FromFrameTimes(frames, "test", 1, 2.0);

                Assert(result.IsMeasured, "замер должен быть измеренным");
                AssertNear(result.AverageFps, 60.0, 0.01, "средний FPS");
                AssertNear(result.MedianFrameMs, 1000.0 / 60.0, 0.01, "медиана кадра");
                Assert(result.Stutters == 0, "фризов быть не должно, найдено " + result.Stutters);
            });

            RunTest(output, "FPS: медленные кадры снижают 1% low и считаются фризами", ref passed, ref failed, () =>
            {
                var frames = Enumerable.Repeat(1000.0 / 100.0, 300).ToList();
                frames.Add(120);
                frames.Add(140);
                frames.Add(160);

                FpsResult result = FpsStats.FromFrameTimes(frames, "test", 1, 5.0);

                Assert(result.OnePercentLowFps < result.AverageFps, "1% low должен быть ниже среднего");
                Assert(result.Stutters >= 3, "три медленных кадра должны считаться фризами");
                Assert(result.MaxFrameMs >= 120, "максимальный кадр должен быть не меньше 120 мс");
            });

            RunTest(output, "FPS: разница 2% честно называется погрешностью", ref passed, ref failed, () =>
            {
                FpsResult before = FpsStats.FromFrameTimes(
                    Enumerable.Repeat(10.0, 200).ToList(), "test", 1, 5.0);
                FpsResult after = FpsStats.FromFrameTimes(
                    Enumerable.Repeat(9.8, 200).ToList(), "test", 1, 5.0);

                FpsComparison comparison = FpsStats.Compare(before, after);
                Assert(comparison.IsWithinNoise, "разница 2% должна быть в пределах шума");
                Assert(comparison.Verdict.Contains("погрешност"), "вердикт должен объяснять погрешность");
            });

            RunTest(output, "FPS: разница 10% — измеренный прирост, а не шум", ref passed, ref failed, () =>
            {
                FpsResult before = FpsStats.FromFrameTimes(
                    Enumerable.Repeat(10.0, 200).ToList(), "test", 1, 5.0);
                FpsResult after = FpsStats.FromFrameTimes(
                    Enumerable.Repeat(9.09, 200).ToList(), "test", 1, 5.0);

                FpsComparison comparison = FpsStats.Compare(before, after);
                Assert(!comparison.IsWithinNoise, "10% выше порога 3%");
                AssertNear(comparison.DeltaPercent, 10.0, 0.2, "процент прироста");
                Assert(comparison.Verdict.Contains("прирост"), "вердикт должен содержать измеренный прирост");
            });

            RunTest(output, "FPS: короткий замер не сравнивается", ref passed, ref failed, () =>
            {
                FpsResult before = FpsStats.FromFrameTimes(
                    Enumerable.Repeat(10.0, 50).ToList(), "test", 1, 1.0);
                FpsResult after = FpsStats.FromFrameTimes(
                    Enumerable.Repeat(10.0, 50).ToList(), "test", 1, 1.0);

                FpsComparison comparison = FpsStats.Compare(before, after);
                Assert(comparison.IsWithinNoise, "замер короче 3 секунд не должен объявляться приростом");
                Assert(comparison.Verdict.Contains("3 секунд"), "вердикт должен объяснять причину");
            });

            RunTest(output, "Строки параметров: чужие значения DirectX сохраняются", ref passed, ref failed, () =>
            {
                string updated = TokenString.SetToken("OtherSetting=5;SwapEffectUpgradeEnable=0;", "SwapEffectUpgradeEnable", "1");
                Assert(updated.Contains("OtherSetting=5"), "чужой параметр потерян");
                Assert(updated.Contains("SwapEffectUpgradeEnable=1"), "нужный параметр не записан");

                string removed = TokenString.RemoveToken(updated, "SwapEffectUpgradeEnable");
                Assert(removed.Contains("OtherSetting=5"), "чужой параметр потерян при удалении");
                Assert(!removed.Contains("SwapEffectUpgradeEnable"), "свой параметр не удалён");
            });

            RunTest(output, "Флаги совместимости: RUNASADMIN и HIGHDPIAWARE не теряются", ref passed, ref failed, () =>
            {
                string added = TokenString.AddLayerToken("~ RUNASADMIN", "DISABLEDXMAXIMIZEDWINDOWEDMODE");
                Assert(added.Contains("RUNASADMIN"), "RUNASADMIN потерян");
                Assert(added.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE"), "токен не добавлен");
                Assert(added.TrimStart().StartsWith("~"), "маркер ~ должен остаться первым");

                string removed = TokenString.RemoveLayerToken(added, "DISABLEDXMAXIMIZEDWINDOWEDMODE");
                Assert(removed.Contains("RUNASADMIN"), "RUNASADMIN потерян при удалении");
                Assert(!removed.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE"), "токен не удалён");

                string onlyMarker = TokenString.RemoveLayerToken("~ DISABLEDXMAXIMIZEDWINDOWEDMODE", "DISABLEDXMAXIMIZEDWINDOWEDMODE");
                Assert(onlyMarker.Length == 0, "значение из одного маркера должно удаляться целиком");
            });

            RunTest(output, "Реестр: откат удаляет значение, которого не было", ref passed, ref failed, () =>
            {
                string sandboxPath = @"Software\MotionCommander\Tests\" + Guid.NewGuid().ToString("N");
                var target = RegistryTarget.Hkcu(sandboxPath, "Value");
                try
                {
                    RegistrySnapshot snapshot = RegistryIo.Capture(target);
                    Assert(!snapshot.Exists, "значение не должно существовать до записи");

                    Assert(RegistryIo.TryWrite(target, 7, RegistryValueKind.DWord, out string writeError),
                        "запись не удалась: " + writeError);
                    Assert(RegistryIo.ReadInt(target) == 7, "значение не записалось");

                    Assert(RegistryIo.Restore(snapshot, out string restoreError),
                        "откат не удался: " + restoreError);
                    Assert(!RegistryIo.Read(target).exists, "значение должно быть удалено при откате");
                }
                finally
                {
                    try { Registry.CurrentUser.DeleteSubKeyTree(sandboxPath, false); } catch { }
                }
            });

            RunTest(output, "Реестр: откат возвращает прежнее значение", ref passed, ref failed, () =>
            {
                string sandboxPath = @"Software\MotionCommander\Tests\" + Guid.NewGuid().ToString("N");
                var target = RegistryTarget.Hkcu(sandboxPath, "Mode");
                try
                {
                    Assert(RegistryIo.TryWrite(target, 1, RegistryValueKind.DWord, out _), "подготовка не удалась");
                    RegistrySnapshot snapshot = RegistryIo.Capture(target);

                    Assert(RegistryIo.TryWrite(target, 2, RegistryValueKind.DWord, out _), "вторая запись не удалась");
                    Assert(RegistryIo.ReadInt(target) == 2, "значение должно быть 2");

                    Assert(RegistryIo.Restore(snapshot, out string restoreError),
                        "откат не удался: " + restoreError);
                    Assert(RegistryIo.ReadInt(target) == 1, "должно вернуться исходное значение 1");
                }
                finally
                {
                    try { Registry.CurrentUser.DeleteSubKeyTree(sandboxPath, false); } catch { }
                }
            });

            RunTest(output, "INI: MK11 — меняются только существующие ключи, откат побайтовый", ref passed, ref failed, () =>
            {
                string file = Path.Combine(testTmp, "GameUserSettings.ini");
                const string original =
                    "[ScalabilitySettings]\n" +
                    "sg.ResolutionQuality=100.000000\n" +
                    "sg.ShadowQuality=3\n" +
                    "sg.ShadowQualityExtra=7\n" +
                    "sg.PostProcessQuality=3\n" +
                    "sg.AntiAliasingQuality=3\n" +
                    "sg.EffectsQuality=3\n" +
                    "sg.FoliageQuality=3\n" +
                    "[/Script/Engine.GameUserSettings]\n" +
                    "bUseVSync=True\n" +
                    "FrameRateLimit=60.000000\n" +
                    "FullscreenMode=1\n";

                File.WriteAllText(file, original, new UTF8Encoding(false));
                byte[] originalBytes = File.ReadAllBytes(file);

                TweakOperationResult result = GameConfigPresets.ApplyToFile(GameConfigPresets.Mk11Fps, file, 144);
                Assert(result.Ok, "применение пресета не удалось: " + result.Message);

                string updated = File.ReadAllText(file);
                Assert(updated.Contains("sg.ShadowQuality=1"), "тени не изменились");
                Assert(updated.Contains("bUseVSync=False"), "VSync не выключен");
                Assert(updated.Contains("FrameRateLimit=144"), "лимит кадров не равен частоте экрана");
                Assert(updated.Contains("sg.ShadowQualityExtra=7"), "похожий ключ изменён — это ошибка");

                string? backup = GameConfigPresets.FindLatestBackupForFile(file);
                Assert(backup != null && File.Exists(backup), "резервная копия не создана");

                TweakOperationResult restore = GameConfigPresets.RestoreFile(file);
                Assert(restore.Ok, "восстановление не удалось: " + restore.Message);
                Assert(File.ReadAllBytes(file).SequenceEqual(originalBytes), "файл после отката не совпадает побайтово");
            });

            RunTest(output, "INI: если ни одного ключа нет — файл не меняется", ref passed, ref failed, () =>
            {
                string file = Path.Combine(testTmp, "NoKeys.ini");
                const string original = "[Section]\nSomeOtherKey=5\n";
                File.WriteAllText(file, original, new UTF8Encoding(false));

                TweakOperationResult result = GameConfigPresets.ApplyToFile(GameConfigPresets.Mk11Fps, file, 60);
                Assert(!result.Ok, "применение должно быть отклонено");
                Assert(result.Message.Contains("не найден") || result.Message.Contains("не изменён"),
                    "сообщение должно объяснять отказ");
                Assert(File.ReadAllText(file) == original, "файл не должен меняться");
                Assert(GameConfigPresets.FindLatestBackupForFile(file) == null, "резервная копия не нужна");
            });

            RunTest(output, "KeyValues: CS2 — меняются только существующие параметры", ref passed, ref failed, () =>
            {
                string file = Path.Combine(testTmp, "cs2_video.txt");
                const string original =
                    "\"VideoConfig\"\n" +
                    "{\n" +
                    "\t\"setting.defaultres\"\t\t\"1920\"\n" +
                    "\t\"setting.mat_vsync\"\t\t\"1\"\n" +
                    "\t\"setting.mat_tripplebuffered\"\t\t\"1\"\n" +
                    "\t\"setting.fullscreen\"\t\t\"0\"\n" +
                    "\t\"setting.r_low_latency\"\t\t\"0\"\n" +
                    "\t\"setting.shaderquality\"\t\t\"2\"\n" +
                    "}\n";

                File.WriteAllText(file, original, new UTF8Encoding(false));

                TweakOperationResult result = GameConfigPresets.ApplyToFile(GameConfigPresets.Cs2Fps, file, 60);
                Assert(result.Ok, "применение CS2-пресета не удалось: " + result.Message);

                string updated = File.ReadAllText(file);
                Assert(Regex.IsMatch(updated, "\"setting\\.mat_vsync\"\\s+\"0\""), "VSync не выключен");
                Assert(Regex.IsMatch(updated, "\"setting\\.r_low_latency\"\\s+\"2\""), "Reflex не включён");
                Assert(Regex.IsMatch(updated, "\"setting\\.shaderquality\"\\s+\"2\""), "посторонний параметр изменён");
                Assert(Regex.IsMatch(updated, "\"setting\\.defaultres\"\\s+\"1920\""), "разрешение изменено — этого быть не должно");
            });

            RunTest(output, "Конфиг: отсутствующий файл честно показывается как отсутствующий", ref passed, ref failed, () =>
            {
                string missing = Path.Combine(testTmp, "missing-game-config.ini");
                ConfigPreview preview = GameConfigPresets.BuildPreviewForFile(GameConfigPresets.Mk11Fps, missing, 60);

                Assert(!preview.FileExists, "файла нет — предпросмотр не должен его выдумывать");
                Assert(preview.Summary.Contains("не найден"), "сводка должна сообщать об отсутствии файла");
                Assert(preview.FoundCount == 0, "ключей не может быть найдено");
            });

            RunTest(output, "Железо: реальные CPU, RAM и экран читаются, а не подставляются", ref passed, ref failed, () =>
            {
                HardwareInfo hardware = HardwareProbe.Probe();
                output.WriteLine();
                output.WriteLine("  CPU:  " + hardware.CpuLine);
                output.WriteLine("  RAM:  " + hardware.RamLine);
                output.WriteLine("  GPU:  " + hardware.GpuLine);
                output.WriteLine("  Экран: " + hardware.DisplayLine);
                output.WriteLine("  ОС:   " + hardware.OsLine);
                output.WriteLine();

                Assert(!string.IsNullOrWhiteSpace(hardware.CpuName), "имя CPU пустое");
                Assert(hardware.CpuThreads > 0, "число потоков CPU не прочитано");
                Assert(hardware.RamGb is > 0, "объём ОЗУ не прочитан");
                Assert(hardware.WindowsBuild > 0, "сборка Windows не прочитана");
                Assert(hardware.DisplayWidth > 0 && hardware.DisplayHeight > 0, "разрешение экрана не прочитано");
            });

            RunTest(output, "ETW FPS: без прав администратора — честный отказ, без цифр", ref passed, ref failed, () =>
            {
                if (FpsMonitor.CanMeasure)
                {
                    output.Write("(процесс уже с правами — отказ проверить нельзя, пропуск) ");
                    return;
                }

                try
                {
                    FpsMonitor.MeasureAsync(Environment.ProcessId, TimeSpan.FromSeconds(3))
                              .GetAwaiter().GetResult();
                    throw new Exception("замер без прав не должен возвращать результат");
                }
                catch (FpsUnavailableException ex)
                {
                    Assert(ex.Message.Contains("администратор"), "сообщение должно объяснять причину");
                    Assert(!Regex.IsMatch(ex.Message, @"\d+([.,]\d+)?\s*FPS"), "в отказе не должно быть выдуманных FPS");
                }
            });

            RunTest(output, "Каталог: состояния читаются, рекомендации имеют измеренное основание", ref passed, ref failed, () =>
            {
                GameTuningService service = GameTuningService.Instance;
                HardwareInfo hardware = HardwareProbe.Probe();

                foreach (GameTweak tweak in service.Tweaks)
                {
                    TweakStatusInfo status = service.GetStatus(tweak, null);
                    Assert(status.Status != TweakStatus.Unknown,
                        $"{tweak.Id}: состояние неизвестно ({status.Detail})");
                }

                TweakStatusInfo power = service.GetStatus(
                    service.Tweaks.First(t => t.Id == GameTweakCatalog.IdPower), null);
                Assert(!string.IsNullOrWhiteSpace(power.Current), "активная схема питания должна читаться");

                List<GameRecommendation> recommendations = service.BuildRecommendations(hardware, null);
                foreach (GameRecommendation recommendation in recommendations)
                {
                    Assert(!string.IsNullOrWhiteSpace(recommendation.Basis),
                        $"{recommendation.TweakId}: нет измеренного основания");
                    Assert(service.Tweaks.Any(t => t.Id == recommendation.TweakId),
                        $"{recommendation.TweakId}: рекомендация ссылается на несуществующий твик");
                }
            });

            RunTest(output, "Каталог: твики с правами администратора не применяются без прав", ref passed, ref failed, () =>
            {
                if (GameTuningService.IsElevated)
                {
                    output.Write("(процесс с правами — проверка не требуется, пропуск) ");
                    return;
                }

                GameTweak? hags = GameTuningService.Instance.Tweaks
                    .FirstOrDefault(t => t.Id == GameTweakCatalog.IdHags);
                Assert(hags != null, "твик HAGS отсутствует в каталоге");

                TweakOperationResult result = GameTuningService.Instance.Apply(hags!, null);
                Assert(!result.Ok, "применение без прав должно быть отклонено");
                Assert(result.Message.Contains("администратор"), "сообщение должно требовать права");
            });

            RunTest(output, "Откат: без резервных копий честно сообщает, что откатывать нечего", ref passed, ref failed, () =>
            {
                int before = GameTuningService.Instance.BackupCount;
                if (before > 0)
                {
                    output.Write($"(в профиле уже {before} резервных копий — проверка пропущена) ");
                    return;
                }

                List<(string TweakId, string Message)> results = GameTuningService.Instance.RevertAll();
                Assert(results.Count > 0, "отчёт об откате пуст");
                Assert(results[0].Message.Contains("Нечего откатывать"), "нет честного сообщения об отсутствии копий");
            });
        }
        finally
        {
            try { Directory.Delete(testTmp, true); } catch { }
        }

        output.WriteLine();
        output.WriteLine(new string('-', 58));
        output.WriteLine($"ИТОГО: пройдено {passed}, провалено {failed}");
        output.WriteLine(new string('-', 58));
        return failed;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void AssertNear(double actual, double expected, double tolerance, string what)
    {
        if (Math.Abs(actual - expected) > tolerance)
            throw new Exception($"{what}: ожидалось {expected:0.###} ± {tolerance:0.###}, получено {actual:0.###}");
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
