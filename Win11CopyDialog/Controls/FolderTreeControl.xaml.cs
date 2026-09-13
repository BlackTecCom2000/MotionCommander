using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Controls;

public sealed partial class FolderTreeControl : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty RootsProperty =
        DependencyProperty.Register(nameof(Roots), typeof(ObservableCollection<FileEntry>), typeof(FolderTreeControl),
            new PropertyMetadata(new ObservableCollection<FileEntry>()));

    public ObservableCollection<FileEntry> Roots
    {
        get => (ObservableCollection<FileEntry>)GetValue(RootsProperty);
        set => SetValue(RootsProperty, value);
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

    public event PropertyChangedEventHandler? PropertyChanged;
}