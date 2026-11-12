using System.Windows;
using System.Windows.Controls;

namespace Soundsys.App.Views;

/// <summary>Phones: paired phones with an on/off toggle and their live state.</summary>
public partial class PhonesPage : UserControl
{
    public PhonesPage()
    {
        InitializeComponent();
    }

    private void OpenBluetooth_Click(object sender, RoutedEventArgs e) => Shell.OpenUri("ms-settings:bluetooth");
}
