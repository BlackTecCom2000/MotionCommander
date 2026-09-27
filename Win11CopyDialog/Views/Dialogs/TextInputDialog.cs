using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Win11CopyDialog.Helpers;

namespace Win11CopyDialog.Views.Dialogs;

/// <summary>
/// Минималистичное модальное окно ввода одной строки текста.
/// Используется там, где раньше стояли пустые тела обработчиков
/// с комментарием «реализация…».
/// </summary>
public sealed class TextInputDialog : Window
{
    private readonly TextBox _input;
    private string _result = "";

    public TextInputDialog(string title, string prompt, string initialValue = "", string okText = "OK")
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.SingleBorderWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(16) };

        root.Children.Add(new TextBlock
        {
            Text = prompt,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
            Foreground = TryFindResource("PrimaryTextBrush") as Brush ?? Brushes.Black
        });

        _input = new TextBox
        {
            Text = initialValue,
            Margin = new Thickness(0, 0, 0, 14),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13
        };
        _input.SelectAll();
        _input.KeyDown += OnKeyDown;
        root.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var ok = new Button
        {
            Content = okText,
            Width = 110,
            Height = 32,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        ok.Click += (_, _) => Accept();

        var cancel = new Button
        {
            Content = "Отмена",
            Width = 110,
            Height = 32,
            IsCancel = true
        };
        cancel.Click += (_, _) =>
        {
            _result = "";
            DialogResult = false;
            Close();
        };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => _input.Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Accept();
    }

    private void Accept()
    {
        _result = _input.Text.Trim();
        DialogResult = true;
        Close();
    }

    /// <summary>Показывает окно и возвращает введённое значение либо пустую строку.</summary>
    public static string Ask(Window? owner, string title, string prompt, string initialValue = "", string okText = "OK")
    {
        var dlg = new TextInputDialog(title, prompt, initialValue, okText);
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        return dlg.ShowDialog() == true ? dlg._result : "";
    }
}
