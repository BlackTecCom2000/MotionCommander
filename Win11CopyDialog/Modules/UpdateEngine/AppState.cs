using System.Windows;

namespace Win11CopyDialog.Modules.UpdateEngine;

public class AppState
{
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public WindowState WindowState { get; set; } = WindowState.Normal;
    public int ActiveTabIndex { get; set; } = 0;
}
