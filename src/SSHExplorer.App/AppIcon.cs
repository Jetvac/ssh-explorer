using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SSHExplorer.App;

/// <summary>
/// The SSH Explorer application mark (assets/brand/*.svg → Assets/AppIcon.ico, 16–256 px).
/// The same ICO is the EXE resource (ApplicationIcon), so Explorer, the taskbar and Alt+Tab use
/// it; windows set it explicitly so owned dialogs and "О приложении" show the same mark.
/// </summary>
public static class AppIcon
{
    static readonly Uri Source = new("pack://application:,,,/SSHExplorer;component/Assets/AppIcon.ico", UriKind.Absolute);
    static BitmapFrame? frame;

    public static ImageSource? Image
    {
        get
        {
            if (frame is not null) return frame;
            try
            {
                var decoder = BitmapDecoder.Create(Source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                if (frame.CanFreeze) frame.Freeze();
            }
            catch (Exception) { frame = null; }
            return frame;
        }
    }

    /// <summary>Best frame for a target size in DIP (the ICO keeps the hand-tuned small variants).</summary>
    public static ImageSource? ImageForSize(double dip, double scale = 1)
    {
        try
        {
            var decoder = BitmapDecoder.Create(Source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            int wanted = (int)Math.Ceiling(dip * scale);
            var best = decoder.Frames.Where(f => f.PixelWidth >= wanted).OrderBy(f => f.PixelWidth).FirstOrDefault() ?? decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            if (best.CanFreeze) best.Freeze();
            return best;
        }
        catch (Exception) { return null; }
    }

    public static void Apply(Window window)
    {
        if (window.Icon is null && Image is { } image) window.Icon = image;
    }
}
