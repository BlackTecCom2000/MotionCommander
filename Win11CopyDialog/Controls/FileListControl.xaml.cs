using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Controls;

public sealed partial class FileListControl : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(nameof(Items), typeof(ObservableCollection<FileEntry>), typeof(FileListControl),
            new PropertyMetadata(new ObservableCollection<FileEntry>(), OnItemsChanged));

    public static readonly DependencyProperty CurrentPathProperty =
        DependencyProperty.Register(nameof(CurrentPath), typeof(string), typeof(FileListControl),
            new PropertyMetadata("", OnPathChanged));

    public ObservableCollection<FileEntry> Items
    {
        get => (ObservableCollection<FileEntry>)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public string CurrentPath
    {
        get => (string)GetValue(CurrentPathProperty);
        set => SetValue(CurrentPathProperty, value);
    }

    public ICommand OpenCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand CutCommand { get; }
    public ICommand PasteCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand PropertiesCommand { get; }
    public ICommand SortCommand { get; }

    public event Action<FileEntry>? ItemActivated;
    public event Action<FileEntry[]>? SelectionChangedEvent;
    public event Action<string>? PathChanged;

    private string _sortProperty = "Name";
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;
    private readonly List<FileEntry> _clipboard = new();

    public FileListControl()
    {
        InitializeComponent();
        DataContext = this;

        OpenCommand = new RelayCommand<FileEntry>(Open);
        CopyCommand = new RelayCommand(() => CopyCut(false));
        CutCommand = new RelayCommand(() => CopyCut(true));
        PasteCommand = new RelayCommand(Paste, () => _clipboard.Count > 0);
        RenameCommand = new RelayCommand<FileEntry>(Rename);
        DeleteCommand = new RelayCommand<FileEntry>(Delete);
        PropertiesCommand = new RelayCommand<FileEntry>(Properties);
        SortCommand = new RelayCommand<string>(Sort);

        FileListView.SelectionChanged += (_, _) => 
            SelectionChangedEvent?.Invoke(FileListView.SelectedItems.Cast<FileEntry>().ToArray());
    }

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FileListControl ctl)
        {
            ctl.EmptyText.Visibility = ctl.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ctl.ApplySort();
        }
    }

    private static void OnPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FileListControl ctl)
            ctl.PathChanged?.Invoke(ctl.CurrentPath);
    }

    private void ApplySort()
    {
        var view = CollectionViewSource.GetDefaultView(Items);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(_sortProperty, _sortDirection));
    }

    private void Sort(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return;
        if (_sortProperty == propertyName)
            _sortDirection = _sortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        else
        {
            _sortProperty = propertyName;
            _sortDirection = ListSortDirection.Ascending;
        }
        ApplySort();
    }

    private void FileListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileListView.SelectedItem is FileEntry entry)
            ItemActivated?.Invoke(entry);
    }

    private void FileListView_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FileListView.SelectedItem is FileEntry enterEntry)
            ItemActivated?.Invoke(enterEntry);
        else if (e.Key == Key.Delete && FileListView.SelectedItems.Count > 0)
            Delete(FileListView.SelectedItems.Cast<FileEntry>().First());
        else if (e.Key == Key.F2 && FileListView.SelectedItem is FileEntry renameEntry)
            Rename(renameEntry);
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            CopyCut(false);
        else if (e.Key == Key.X && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            CopyCut(true);
        else if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            Paste();
    }

    private void FileListView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (FileListView.SelectedItems.Count == 0)
            e.Handled = true;
    }

    private void Open(FileEntry? entry)
    {
        if (entry != null) ItemActivated?.Invoke(entry);
    }

    private void CopyCut(bool isCut)
    {
        _clipboard.Clear();
        foreach (var item in FileListView.SelectedItems.Cast<FileEntry>())
            _clipboard.Add(item);
        // В реальной реализации: запомнить isCut для Paste
    }

    private void Paste()
    {
        // Реализация вставки
    }

    private void Rename(FileEntry? entry)
    {
        if (entry == null) return;
        // Инлайн редактирование имени
    }

    private void Delete(FileEntry? entry)
    {
        if (entry == null) return;
        try
        {
            if (entry.Type == Models.FileEntryType.Folder)
                Directory.Delete(entry.FullPath, true);
            else
                File.Delete(entry.FullPath);
            Items.Remove(entry);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось удалить: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Properties(FileEntry? entry)
    {
        if (entry == null) return;
        var msg = $"Имя: {entry.Name}\nПуть: {entry.FullPath}\nТип: {entry.Type}\nРазмер: {entry.SizeText}\nСоздан: {entry.CreationTime}\nИзменен: {entry.LastWriteTime}";
        MessageBox.Show(msg, "Свойства", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<bool>? _canExecute;
    public RelayCommand(Action<T?> execute, Func<bool>? canExecute = null) { _execute = execute; _canExecute = canExecute; }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute((T?)parameter);
}

internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;
    public RelayCommand(Action execute, Func<bool>? canExecute = null) { _execute = execute; _canExecute = canExecute; }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
}