using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Controls;

public sealed partial class FolderTreeControl : UserControl, INotifyPropertyChanged
{
    // ВАЖНО: значение по умолчанию для коллекции в PropertyMetadata указывать НЕЛЬЗЯ.
    // WPF не копирует объект по умолчанию — все экземпляры FolderTreeControl
    // получали ОДИН И ТОТ ЖЕ экземпляр коллекции. Из-за этого открытие второго
    // окна файлового менеджера вызывало Roots.Clear() и дерево дисков в первом
    // окне становилось пустым.
    // Решение: nullable-DP + ленивое создание собственной коллекции в геттере.
    public static readonly DependencyProperty RootsProperty =
        DependencyProperty.Register(nameof(Roots), typeof(ObservableCollection<FileEntry>), typeof(FolderTreeControl),
            new PropertyMetadata(null));

    private ObservableCollection<FileEntry>? _roots;

    public ObservableCollection<FileEntry> Roots
    {
        get => _roots ??= (ObservableCollection<FileEntry>?)GetValue(RootsProperty) ?? new ObservableCollection<FileEntry>();
        set
        {
            SetValue(RootsProperty, value);
            RaisePropertyChanged();
        }
    }

    public ICommand NewFolderCommand { get; }
    public ICommand PasteCommand { get; }

    public event Action<FileEntry>? FolderSelected;

    public FolderTreeControl()
    {
        InitializeComponent();
        DataContext = this;
        NewFolderCommand = new RelayCommand<FileEntry>(CreateNewFolder);
        PasteCommand = new RelayCommand(() => { /* paste logic */ });
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FileEntry entry)
            FolderSelected?.Invoke(entry);
    }

    private void FolderTree_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is FileEntry entry)
        {
            entry.IsSelected = true;
            e.Handled = true;
        }
    }

    private void CreateNewFolder(FileEntry? parent)
    {
        if (parent == null) return;
        var newPath = System.IO.Path.Combine(parent.FullPath, "Новая папка");
        int i = 1;
        while (Directory.Exists(newPath))
            newPath = System.IO.Path.Combine(parent.FullPath, $"Новая папка ({i++})");
        
        try
        {
            Directory.CreateDirectory(newPath);
            var newEntry = new FileEntry(newPath, FileEntryType.Folder, DateTime.Now, DateTime.Now, FileAttributes.Directory);
            newEntry.SetIcon(FileSystemIcons.GetIcon(newPath, FileEntryType.Folder));
            parent.Children.Add(newEntry);
            parent.IsExpanded = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось создать папку: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task LoadDrivesAsync()
    {
        var drives = await FileService.GetDrivesAsync();
        Roots.Clear();
        foreach (var drive in drives)
            Roots.Add(drive);
    }

    /// <summary>
    /// Событие было объявлено, но никогда не возбуждалось (предупреждение CS0067),
    /// то есть реализация INotifyPropertyChanged была декоративной.
    /// Теперь оно действительно работает, что нужно биндингам в XAML.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaisePropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}