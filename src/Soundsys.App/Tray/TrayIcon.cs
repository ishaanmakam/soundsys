using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using Soundsys.App.Theme;
using Soundsys.App.ViewModels;
using Soundsys.Engine.Settings;

namespace Soundsys.App.Tray;

/// <summary>
/// The notification-area icon: runtime-drawn rings, the headline as tooltip, and a menu:
/// Open soundsys · Start/Stop · Source ▸ Phone/Laptop · Quit.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly NotifyIcon _notify;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _startStop;
    private readonly ToolStripMenuItem _phone;
    private readonly ToolStripMenuItem _laptop;
    private Icon? _icon;
    private IntPtr _iconHandle;
    private (TrayVisual Visual, bool Dark, int Size)? _drawn;

    public TrayIcon(MainViewModel vm, Action open, Action quit)
    {
        _vm = vm;
        _menu = new ContextMenuStrip { ShowImageMargin = false };
        var openItem = new ToolStripMenuItem("Open soundsys", null, (_, _) => open()) { Font = new Font(_menu.Font, System.Drawing.FontStyle.Bold) };
        _startStop = new ToolStripMenuItem("Start", null, async (_, _) =>
        {
            if (_vm.IsRunning)
            {
                await _vm.StopAsync();
            }
            else
            {
                await _vm.StartAsync();
            }
        });
        _phone = new ToolStripMenuItem("Phone", null, (_, _) => _vm.SetModeCommand.Execute(SourceMode.Phone));
        _laptop = new ToolStripMenuItem("Laptop", null, (_, _) => _vm.SetModeCommand.Execute(SourceMode.Laptop));
        var source = new ToolStripMenuItem("Source");
        source.DropDownItems.AddRange([_phone, _laptop]);
        _menu.Items.AddRange([openItem, _startStop, source, new ToolStripSeparator(), new ToolStripMenuItem("Quit", null, (_, _) => quit())]);

        _notify = new NotifyIcon { ContextMenuStrip = _menu, Text = "soundsys" };
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                open();
            }
        };
        _notify.BalloonTipClicked += (_, _) => open();

        _vm.PropertyChanged += OnViewModelChanged;
        ThemeManager.Changed += OnThemeChanged;
        Update();
        _notify.Visible = true;
    }

    public void ShowBalloon(string title, string text) => _notify.ShowBalloonTip(5000, title, text, ToolTipIcon.None);

    public void Update()
    {
        var size = SystemInformation.SmallIconSize.Width;
        var key = (_vm.Tray, ThemeManager.SystemTaskbarDark(), size);
        if (_drawn != key)
        {
            var (icon, handle) = RingIconRenderer.CreateIcon(size, key.Item1, key.Item2);
            _notify.Icon = icon;
            RingIconRenderer.Free(_icon, _iconHandle);
            (_icon, _iconHandle, _drawn) = (icon, handle, key);
        }

        _notify.Text = string.IsNullOrEmpty(_vm.TrayText) ? "soundsys" : _vm.TrayText;
        _startStop.Text = _vm.IsRunning ? "Stop" : "Start";
        _startStop.Enabled = _vm.CanToggleRun;
        _phone.Checked = _vm.IsPhoneMode;
        _laptop.Checked = _vm.IsLaptopMode;
    }

    public void Dispose()
    {
        _vm.PropertyChanged -= OnViewModelChanged;
        ThemeManager.Changed -= OnThemeChanged;
        _notify.Visible = false;
        _notify.Dispose();
        _menu.Dispose();
        RingIconRenderer.Free(_icon, _iconHandle);
        _icon = null;
        _iconHandle = IntPtr.Zero;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Update();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Tray) or nameof(MainViewModel.TrayText) or nameof(MainViewModel.IsRunning)
            or nameof(MainViewModel.CanToggleRun) or nameof(MainViewModel.IsPhoneMode) or nameof(MainViewModel.IsLaptopMode))
        {
            Update();
        }
    }
}
