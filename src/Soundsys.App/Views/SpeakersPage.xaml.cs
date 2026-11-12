using System.Windows;
using System.Windows.Controls;

namespace Soundsys.App.Views;

/// <summary>Speakers: which two speakers, and what to call them.</summary>
public partial class SpeakersPage : UserControl
{
    public SpeakersPage()
    {
        InitializeComponent();
    }

    private void OpenBluetooth_Click(object sender, RoutedEventArgs e) => Shell.OpenUri("ms-settings:bluetooth");
}
