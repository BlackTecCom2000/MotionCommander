using System.Windows;
using System.Windows.Media;
using Win11CopyDialog.Helpers;
using Win11CopyDialog.Models;
using Win11CopyDialog.Modules.SafetyEngine.Services;

namespace Win11CopyDialog.Modules.SafetyEngine.Views;

public partial class ImpactPreviewDialog : Window
{
    public ImpactAssessment Assessment { get; }

    public ImpactPreviewDialog(ImpactAssessment assessment)
    {
        InitializeComponent();
        Assessment = assessment;

        BackdropHelper.Apply(this, ThemeManager.Instance.Backdrop, ThemeManager.Instance.IsDark);

        ImpactTitle.Text = assessment.Title;
        TargetSummaryText.Text = assessment.TargetSummary;

        RiskBadgeText.Text = assessment.RiskLevel;
        try
        {
            RiskBadge.Background = new BrushConverter().ConvertFromString(assessment.RiskBadgeColorHex) as Brush;
        }
        catch { }

        ReversibilityText.Text = assessment.ReversibilityExplanation;
        ReversibilityText.Foreground = assessment.IsReversible
            ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
            : new SolidColorBrush(Color.FromRgb(239, 68, 68));

        ImpactDetailsList.ItemsSource = assessment.ImpactDetails;

        if (assessment.HasSystemFilesWarning)
        {
            SystemWarningCard.Visibility = Visibility.Visible;
            SystemWarningText.Text = assessment.SystemFilesWarning;
        }

        EmergencyPlanText.Text = assessment.EmergencyPlan;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        HapticAudio.PlayClick();
        DialogResult = false;
        Close();
    }
}
