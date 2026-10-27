using System.Windows;
using System.Windows.Media;
using Win11CopyDialog.Models;

namespace Win11CopyDialog.Views.Dialogs;

public partial class ThemePreviewWindow : Window
{
    public ThemePreviewWindow()
    {
        InitializeComponent();
        // Initialize preview panels with two themes: Dark MotionGlass and Light MotionGlassLight
        ApplyPreview(AppTheme.MotionGlass, LeftCard, LeftSampleText, LeftBtn, LeftTitle);
        ApplyPreview(AppTheme.MotionGlassLight, RightCard, RightSampleText, RightBtn, RightTitle);
        // Optional: set title text color to be contrast-friendly
    }

    private void ApplyPreview(AppTheme t, System.Windows.Controls.Border card, System.Windows.Controls.TextBlock sampleText, System.Windows.Controls.Button btn, System.Windows.Controls.TextBlock title)
    {
        var colors = ThemeManager.Instance.GetColors(t);
        // Set panel card color and border, text and accent
        card.Background = new SolidColorBrush(colors.Card);
        card.BorderBrush = new SolidColorBrush(colors.Border);
        // Title/text color
        title.Text = ThemeManager.Instance.ThemeDisplayName(t);
        title.Foreground = new SolidColorBrush(colors.Text);
        sampleText.Text = "Sample text on card";
        sampleText.Foreground = new SolidColorBrush(colors.TextMuted);
        // Accent color for button
        btn.Background = new SolidColorBrush(colors.Accent);
        // Compute a readable foreground for the button text
        var accentText = ThemeManager.Instance.AccentAsText(colors);
        btn.Foreground = new SolidColorBrush(accentText);
    }
}
