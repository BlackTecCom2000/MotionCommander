using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Win11CopyDialog.Modules.StorageControlCenter.Models;
using Win11CopyDialog.Modules.StorageControlCenter.Services;

namespace Win11CopyDialog.Modules.StorageControlCenter.Views;

public partial class MigrationWizardView : UserControl
{
    private List<StorageDisk> _allDisks = new();
    private StorageDisk? _sourceDisk;
    private MigrationOrchestratorService? _orchestrator;
    private CancellationTokenSource? _migrationCts;
    private bool _testModeEnabled = false;

    public MigrationWizardView()
    {
        InitializeComponent();
    }

    public void SetDisks(List<StorageDisk> disks)
    {
        _allDisks = disks;
        _sourceDisk = _allDisks.FirstOrDefault(d => d.Partitions.Any(p => p.IsSystem || string.Equals(p.DriveLetter, "C", StringComparison.OrdinalIgnoreCase)));
        
        PopulateTargetDrives();
    }

    private void PopulateTargetDrives()
    {
        TargetDriveCombo.Items.Clear();
        foreach (var disk in _allDisks)
        {
            if (_sourceDisk != null && disk.DiskNumber == _sourceDisk.DiskNumber) continue;

            TargetDriveCombo.Items.Add(new ComboBoxItem
            {
                Content = $"Диск {disk.DiskNumber}: {disk.Model} ({disk.TotalSizeFormatted})",
                Tag = disk
            });
        }
        
        if (TargetDriveCombo.Items.Count > 0)
        {
            TargetDriveCombo.SelectedIndex = 0;
        }
    }

    private void TestMode_Click(object sender, RoutedEventArgs e)
    {
        _testModeEnabled = !_testModeEnabled;
        var btn = (Button)sender;
        btn.Foreground = _testModeEnabled ? new SolidColorBrush(Color.FromRgb(16, 185, 129)) : (Brush)FindResource("MutedTextBrush");
        btn.Content = _testModeEnabled ? "✔ Тестовый режим ВКЛЮЧЕН (Dry Run)" : "Включить тестовый режим (Dry Run)";
        LogMessage($"Тестовый режим (Dry Run) {(_testModeEnabled ? "включен. Операции записи не будут выполняться." : "выключен. Операции будут выполнены РЕАЛЬНО.")}");
    }

    private void LogMessage(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            LogTextBox.ScrollToEnd();
        });
    }

    private void UpdateStepStyle(TextBlock tb, bool isActive, bool isCompleted)
    {
        if (isCompleted)
        {
            tb.Style = (Style)FindResource("CompletedStepTextBlockStyle");
        }
        else if (isActive)
        {
            tb.Style = (Style)FindResource("ActiveStepTextBlockStyle");
        }
        else
        {
            tb.Style = (Style)FindResource("StepTextBlockStyle");
        }
    }

    private void Orchestrator_ProgressChanged(object? sender, MigrationProgressEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            OverallProgressBar.Value = e.OverallProgressPercent;
            OverallProgressPercentText.Text = $"{e.OverallProgressPercent:F0}%";
            
            if (!string.IsNullOrEmpty(e.Message))
            {
                LogMessage(e.Message);
            }

            // Update UI step tracking
            var step = e.CurrentStep;
            
            UpdateStepStyle(Step1Text, step == MigrationStep.Initialize, step > MigrationStep.Initialize);
            UpdateStepStyle(Step2Text, step == MigrationStep.PreflightAnalysis, step > MigrationStep.PreflightAnalysis);
            UpdateStepStyle(Step3Text, step == MigrationStep.TargetPreparation, step > MigrationStep.TargetPreparation);
            UpdateStepStyle(Step4Text, step == MigrationStep.VssSnapshotCreation, step > MigrationStep.VssSnapshotCreation);
            UpdateStepStyle(Step5Text, step == MigrationStep.DataMigration, step > MigrationStep.DataMigration);
            UpdateStepStyle(Step6Text, step == MigrationStep.BootloaderConfiguration, step > MigrationStep.BootloaderConfiguration);
            UpdateStepStyle(Step7Text, step == MigrationStep.Verification, step > MigrationStep.Verification);
            UpdateStepStyle(Step8Text, step == MigrationStep.Finalization, step > MigrationStep.Finalization);

            if (step == MigrationStep.Failed || step == MigrationStep.RolledBack)
            {
                OverallProgressBar.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                OverallProgressPercentText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            }
        });
    }

    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceDisk == null)
        {
            MessageBox.Show("Не найден исходный системный диск.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (TargetDriveCombo.SelectedItem is not ComboBoxItem selectedItem || selectedItem.Tag is not StorageDisk targetDisk)
        {
            MessageBox.Show("Пожалуйста, выберите целевой накопитель.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_testModeEnabled)
        {
            var res = MessageBox.Show(
                $"ВНИМАНИЕ!\n\nЦелевой диск: {targetDisk.Model}\n\nВСЕ ДАННЫЕ НА ЦЕЛЕВОМ ДИСКЕ БУДУТ БЕЗВОЗВРАТНО УНИЧТОЖЕНЫ!\n\nВы уверены?", 
                "Критическое подтверждение", 
                MessageBoxButton.YesNo, 
                MessageBoxImage.Stop);
                
            if (res != MessageBoxResult.Yes) return;
        }

        // Setup UI for run
        StartBtn.IsEnabled = false;
        TargetDriveCombo.IsEnabled = false;
        ConfigurationPanel.IsEnabled = false;
        CancelBtn.IsEnabled = true;
        LogTextBox.Clear();
        OverallProgressBar.Foreground = (Brush)FindResource("AccentBrush");
        OverallProgressPercentText.Foreground = (Brush)FindResource("AccentBrush");

        _migrationCts = new CancellationTokenSource();
        _orchestrator = new MigrationOrchestratorService(_sourceDisk, targetDisk, _testModeEnabled);
        _orchestrator.ProgressChanged += Orchestrator_ProgressChanged;

        try
        {
            bool success = await _orchestrator.StartMigrationAsync(_migrationCts.Token);
            
            if (success)
            {
                MessageBox.Show("Миграция успешно завершена!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Миграция была прервана или завершилась с ошибкой. Проверьте журнал.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (_orchestrator != null)
            {
                _orchestrator.ProgressChanged -= Orchestrator_ProgressChanged;
            }
            
            StartBtn.IsEnabled = true;
            TargetDriveCombo.IsEnabled = true;
            ConfigurationPanel.IsEnabled = true;
            CancelBtn.IsEnabled = false;
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_migrationCts != null && !_migrationCts.IsCancellationRequested)
        {
            var res = MessageBox.Show("Вы действительно хотите отменить миграцию? Это может оставить целевой диск в нестабильном состоянии.", "Отмена", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                CancelBtn.IsEnabled = false;
                LogMessage("Запрошена отмена операции...");
                _migrationCts.Cancel();
            }
        }
    }
}
