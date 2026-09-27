using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Controls;

public sealed partial class FileListControl : UserControl, INotifyPropertyChanged
{
    // ВАЖНО: значение по умолчанию для коллекции в PropertyMetadata указывать НЕЛЬЗЯ.
    // WPF не копирует объект по умолчанию — все экземпляры FileListControl
    // разделяли ОДНУ И ТУ ЖЕ коллекцию, из-за чего изменения в одном окне
    // отражались в других (и наоборот, очистка гасила чужое содержимое).
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(nameof(Items), typeof(ObservableCollection<FileEntry>), typeof(FileListControl),
            new PropertyMetadata(null, OnItemsChanged));

    public static readonly DependencyProperty CurrentPathProperty =
        DependencyProperty.Register(nameof(CurrentPath), typeof(string), typeof(FileListControl),
            new PropertyMetadata("", OnPathChanged));

    private ObservableCollection<FileEntry>? _items;

    public ObservableCollection<FileEntry> Items
    {
        get => _items ??= (ObservableCollection<FileEntry>?)GetValue(ItemsProperty) ?? new ObservableCollection<FileEntry>();
        set
        {
            SetValue(ItemsProperty, value);
            RaisePropertyChanged();
        }
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

    /// <summary>true — буфер заполнен через «Вырезать», false — через «Копировать».</summary>
    private bool _clipboardIsCut;

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

    /// <summary>
    /// Заменяет представление списка (GridView) для переключения между
    /// табличным и плиточным режимом.
    ///
    /// <para>Свойство View существует именно у ListView, а не у
    /// ICollectionView, поэтому его нельзя сменить снаружи контрола —
    /// требуется публичная точка входа.</para>
    /// </summary>
    public void SetView(GridView? view)
    {
        if (FileListView == null) return;

        FileListView.View = view;

        // После смены представления GridView колонки сбрасывают сортировку,
        // поэтому переприменяем её явно.
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
        {
            // Раньше здесь стояло .First(): при множественном выделении
            // удалялся только ПЕРВЫЙ элемент, а остальные молча игнорировались.
            foreach (var entry in FileListView.SelectedItems.Cast<FileEntry>().ToList())
            {
                Delete(entry);
            }
        }
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

    /// <summary>Копирует выделенные элементы в буфер обмена.</summary>
    /// <param name="isCut">true — «вырезать» (исходники удаляются после вставки), false — «копировать».</param>
    private void CopyCut(bool isCut)
    {
        _clipboard.Clear();
        foreach (var item in FileListView.SelectedItems.Cast<FileEntry>())
            _clipboard.Add(item);

        // Раньше параметр isCut просто принимался и НИКУДА не сохранялся
        // (стоял комментарий «в реальной реализации…»), поэтому «Вырезать»
        // и «Копировать» были полностью неразличимы.
        _clipboardIsCut = isCut;

        // L-11: CanExecute зависит от _clipboard, но CommandManager не знает,
        // что состояние изменилось, и пункт «Вставить» оставался неактивным.
        CommandManager.InvalidateRequerySuggested();
    }

    private void Paste()
    {
        // Раньше здесь стоял только комментарий «Реализация вставки».
        // Копирование и вырезание наполняли буфер, но вставить было нечем,
        // и буфер обмена молча устаревал.
        //
        // Само действие выполняет окно-владелец: только у него есть
        // текущий каталог назначения и движок копирования.
        if (_clipboard.Count == 0) return;

        var entries = _clipboard.ToArray();
        PasteRequested?.Invoke(entries, _clipboardIsCut);
    }

    private void Rename(FileEntry? entry)
    {
        if (entry == null) return;

        // Раньше здесь стоял комментарий «Инлайн редактирование имени».
        string oldName = System.IO.Path.GetFileName(entry.FullPath);
        string newName = Views.Dialogs.TextInputDialog.Ask(
            System.Windows.Window.GetWindow(this),
            "Переименовать",
            "Новое имя для «" + oldName + "»:",
            oldName,
            "Переименовать");

        if (string.IsNullOrWhiteSpace(newName)) return;
        if (newName == oldName) return;

        try
        {
            // Защита от выхода из своей папки и от пути-разделителя в имени.
            if (newName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
                newName.Contains('/') || newName.Contains('\\'))
            {
                MessageBox.Show("Имя содержит недопустимые символы.", "Переименование",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (newName is "." or "..")
            {
                MessageBox.Show("Недопустимое имя.", "Переименование",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string dir = System.IO.Path.GetDirectoryName(entry.FullPath) ?? "";
            string newPath = System.IO.Path.Combine(dir, newName);

            if (System.IO.File.Exists(newPath) || System.IO.Directory.Exists(newPath))
            {
                MessageBox.Show("Объект с таким именем уже существует.", "Переименование",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (entry.Type == Models.FileEntryType.Folder)
                System.IO.Directory.Move(entry.FullPath, newPath);
            else
                System.IO.File.Move(entry.FullPath, newPath);

            entry.RenameTo(newPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось переименовать: " + ex.Message, "Переименование",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Запрошено действие «вставить». Аргументы: элементы буфера и признак
    /// «вырезано» (исходники удаляются после успешного копирования).
    /// </summary>
    public event Action<FileEntry[], bool>? PasteRequested;

    private void Delete(FileEntry? entry)
    {
        if (entry == null) return;

        // Подтверждение. Раньше удаление выполнялось сразу по пункту меню,
        // без вопроса, хотя операция необратима.
        var result = MessageBox.Show(
            $"Удалить «{entry.Name}»?\n\nДействие нельзя отменить.",
            "Удаление",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

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

    /// <summary>
    /// Событие было объявлено, но никогда не возбуждалось (предупреждение CS0067).
    /// Теперь оно действительно работает, что нужно биндингам в XAML.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaisePropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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