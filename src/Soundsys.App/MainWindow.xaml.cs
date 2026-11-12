using System.Windows;
using Soundsys.App.ViewModels;

namespace Soundsys.App;

/// <summary>The one window. Closing hides it to the tray; Quit (tray menu) really exits.</summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>Near the tray: bottom-right of the work area.</summary>
    public void PlaceNearTray()
    {
        var area = SystemParameters.WorkArea;
        UpdateLayout();
        var w = ActualWidth > 0 ? ActualWidth : 400;
        var h = ActualHeight > 0 ? ActualHeight : 700;
        Left = area.Right - w - 12;
        Top = Math.Max(area.Top, area.Bottom - h - 12);
    }
}
