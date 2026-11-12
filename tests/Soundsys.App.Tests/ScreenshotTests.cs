using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Soundsys.App.Controls;
using Soundsys.App.Theme;
using Soundsys.App.ViewModels;
using Soundsys.App.Views;
using Soundsys.Engine.Settings;
using Xunit.Abstractions;

namespace Soundsys.App.Tests;

/// <summary>
/// Renders the real window content, bound to real view models over the simulated engine, in every designed state, in
/// light and dark, at 400 × 660 (2x pixels) into screenshots/ (or $SOUNDSYS_SCREENSHOT_DIR). CI uploads them for review.
/// </summary>
public class ScreenshotTests(ITestOutputHelper output)
{
    private const double Width = 400;
    private const double Height = 660;
    private const double Scale = 2;

    private static string OutputDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("SOUNDSYS_SCREENSHOT_DIR");
            return string.IsNullOrWhiteSpace(dir) ? Path.Combine(AppContext.BaseDirectory, "screenshots") : dir;
        }
    }

    [Fact]
    public void Every_state_renders_in_light_and_dark()
    {
        var written = new List<string>();

        Shoot("01-ready", written, _ => { });
        Shoot("02-empty", written, _ => { }, empty: true);
        Shoot("03-connecting", written, rig =>
        {
            rig.Backend.SetSpeakerAvailable(Rig.Desk, false);
            rig.Backend.SetSpeakerAvailable(Rig.Bed, false);
            rig.Run(0.3);
            rig.Engine.Start();
            rig.Run(0.5);
        });
        Shoot("04-playing-phone", written, rig =>
        {
            rig.StartPhonePlaying();
            rig.Engine.SetManualSync(59);
            rig.Run(1);
        });
        Shoot("05-waiting-for-phone", written, rig =>
        {
            rig.Engine.Start();
            rig.Run(1);
        });
        Shoot("06-one-speaker-dropped", written, rig =>
        {
            rig.StartPhonePlaying();
            rig.Backend.SetSpeakerAvailable(Rig.Bed, false);
            rig.Run(4);
        });
        Shoot("07-error", written, rig =>
        {
            rig.StartPhonePlaying();
            rig.Vm.ReportError(DeveloperViewModel.SampleError);
        });
        Shoot("08-warning", written, rig =>
        {
            rig.Engine.SelectSpeaker(Engine.SpeakerSlot.B, Rig.Laptop);
            rig.Vm.SpeakersPage.Reload();
            rig.Vm.Refresh();
        });
        Shoot("09-laptop-playing", written, rig => rig.StartLaptopMode());

        // Calibrating: Home and Sync mid-sweep, then Sync with the result.
        using (var rig = new Rig())
        {
            rig.StartLaptopMode();
            var task = rig.StartCalibration();
            rig.RunUntil(() => rig.Vm.SyncPage.StepText?.Contains("sweep", StringComparison.Ordinal) == true, what: "sweep");
            Render("10-calibrating", rig.Vm, written);
            rig.Vm.SelectedTab = AppTab.Sync;
            Render("11-sync-calibrating", rig.Vm, written);
            rig.Await(task, maxSeconds: 40);
            rig.Vm.Refresh();
            Render("12-sync-result", rig.Vm, written);
        }

        Shoot("13-speakers", written, rig => rig.Vm.SelectedTab = AppTab.Speakers);
        Shoot("14-phones", written, rig =>
        {
            rig.StartPhonePlaying();
            rig.Vm.SelectedTab = AppTab.Phones;
            rig.Vm.PhonesPage.Refresh();
        });
        Shoot("15-settings", written, rig => rig.Vm.SelectedTab = AppTab.Settings);

        foreach (var f in written)
        {
            output.WriteLine(f);
        }

        Assert.Equal(15 * 2, written.Count);
        Assert.All(written, f => Assert.True(new FileInfo(f).Length > 2000, $"{f} looks empty"));
    }

    private static void Shoot(string name, List<string> written, Action<Rig> arrange, bool empty = false)
    {
        using var rig = new Rig(empty: empty);
        arrange(rig);
        rig.Vm.Refresh();
        Render(name, rig.Vm, written);
    }

    private static void Render(string name, MainViewModel vm, List<string> written)
    {
        Directory.CreateDirectory(OutputDir);
        WpfThread.Invoke(() =>
        {
            var app = WpfThread.EnsureApp();
            TwoRings.ForceReducedMotion = true;
            foreach (var dark in new[] { false, true })
            {
                ThemeManager.Apply(app, dark);
                var view = new MainView { DataContext = vm };
                var root = new Border { Width = Width, Height = Height, Child = view };
                root.SetResourceReference(Border.BackgroundProperty, "BackdropBrush");
                TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
                root.Measure(new Size(Width, Height));
                root.Arrange(new Rect(0, 0, Width, Height));
                WpfThread.DoEvents();
                root.UpdateLayout();
                WpfThread.DoEvents();

                var bitmap = new RenderTargetBitmap((int)(Width * Scale), (int)(Height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.Combine(OutputDir, $"{name}-{(dark ? "dark" : "light")}.png");
                using (var stream = File.Create(path))
                {
                    encoder.Save(stream);
                }

                view.DataContext = null; // unbind before the view model changes on the test thread
                written.Add(path);
            }
        });
    }
}
