using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Soundsys.App.ViewModels;

namespace Soundsys.App.Tray;

/// <summary>
/// Draws the tray icon at runtime: two small rings. Amber and overlapping when playing, ink and apart when stopped,
/// alert when a speaker is lost. Colours follow the taskbar's light/dark setting.
/// </summary>
internal static class RingIconRenderer
{
    public static Color ColorFor(TrayVisual visual, bool darkTaskbar) => visual switch
    {
        TrayVisual.Playing => darkTaskbar ? Color.FromArgb(0xF2, 0xA9, 0x3B) : Color.FromArgb(0xB8, 0x6E, 0x00),
        TrayVisual.Alert => darkTaskbar ? Color.FromArgb(0xFF, 0x8A, 0x80) : Color.FromArgb(0xB3, 0x26, 0x1E),
        _ => darkTaskbar ? Color.FromArgb(0xF1, 0xEF, 0xEA) : Color.FromArgb(0x1C, 0x1B, 0x1A),
    };

    public static Bitmap Render(int size, TrayVisual visual, bool darkTaskbar)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        var color = ColorFor(visual, darkTaskbar);
        var stroke = Math.Max(1.5f, size * 0.09f);
        var r = (size * 0.30f) - (stroke / 2);
        var merged = visual != TrayVisual.Stopped;
        var half = merged ? size * 0.15f : size * 0.205f;
        var cy = size / 2f;
        var ax = (size / 2f) - half;
        var bx = (size / 2f) + half;

        if (visual == TrayVisual.Playing)
        {
            // Translucent discs: where they overlap (the lens) the amber doubles up.
            using var fill = new SolidBrush(Color.FromArgb(90, color));
            g.FillEllipse(fill, ax - r, cy - r, 2 * r, 2 * r);
            g.FillEllipse(fill, bx - r, cy - r, 2 * r, 2 * r);
        }

        using var pen = new Pen(color, stroke);
        g.DrawEllipse(pen, ax - r, cy - r, 2 * r, 2 * r);
        g.DrawEllipse(pen, bx - r, cy - r, 2 * r, 2 * r);
        return bmp;
    }

    /// <summary>An <see cref="Icon"/> that owns its HICON; call <see cref="Free"/> when replacing it.</summary>
    public static (Icon Icon, IntPtr Handle) CreateIcon(int size, TrayVisual visual, bool darkTaskbar)
    {
        using var bmp = Render(size, visual, darkTaskbar);
        var handle = bmp.GetHicon();
        return (Icon.FromHandle(handle), handle);
    }

    public static void Free(Icon? icon, IntPtr handle)
    {
        icon?.Dispose();
        if (handle != IntPtr.Zero)
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
