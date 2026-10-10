using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Win11CopyDialog.Modules.Navigation.Services;

namespace Win11CopyDialog.Modules.Navigation.Views;

public partial class GlobalSearchDialog : Window
{
    private readonly string _currentFolder;

    public GlobalSearchDialog(string currentFolder = "")
    {
        InitializeComponent();
        _currentFolder = currentFolder ?? "";

        Loaded += GlobalSearchDialog_Loaded;
    }

    private void GlobalSearchDialog_Loaded(object sender, RoutedEventArgs e)
    {
        SearchInput.Focus();
        RunSearch("");
    }

    private void RunSearch(string query)
    {
        var list = GlobalCommandService.Instance.Search(query, _currentFolder);
        ResultsList.ItemsSource = list;
        if (list.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
        }
    }

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        RunSearch(SearchInput.Text);
    }

    private void SearchInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            if (ResultsList.Items.Count > 0)
            {
                int next = Math.Min(ResultsList.SelectedIndex + 1, ResultsList.Items.Count - 1);
                ResultsList.SelectedIndex = next;
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            if (ResultsList.Items.Count > 0)
            {
                int prev = Math.Max(ResultsList.SelectedIndex - 1, 0);
                ResultsList.SelectedIndex = prev;
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ExecuteSelectedItem();
            e.Handled = true;
        }
    }

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem != null)
        {
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ExecuteSelectedItem();
    }

    private void ExecuteSelectedItem()
    {
        if (ResultsList.SelectedItem is GlobalSearchResultItem item)
        {
            DialogResult = true;
            Close();
            item.ExecuteAction?.Invoke();
        }
    }
}
