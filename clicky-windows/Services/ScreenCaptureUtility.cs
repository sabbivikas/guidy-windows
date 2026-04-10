using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace ClickyWindows.Services;

/// <summary>
/// Captures screenshots of all connected monitors using GDI+.
/// Returns labeled JPEG byte arrays, one per screen, matching the format
/// that the original macOS app sends to Claude.
///
/// Each captured image is labeled "Screen 1 (Primary)", "Screen 2", etc.
/// so Claude can reference them in [POINT:x,y:label:screenN] tags.
/// </summary>
public static class ScreenCaptureUtility
{
    public record CapturedScreen(byte[] JpegData, string Label, Rectangle Bounds, double ScaleFactor);

    /// <summary>
    /// Captures all connected monitors and returns JPEG data + labels.
    /// Quality is set to 85 to balance file size vs clarity for Claude's vision.
    /// </summary>
    public static List<CapturedScreen> CaptureAllScreens()
    {
        var capturedScreens = new List<CapturedScreen>();
        var allScreens = Screen.AllScreens;

        // Sort screens left-to-right so "Screen 1" is the leftmost display
        var sortedScreens = allScreens.OrderBy(s => s.Bounds.X).ToArray();

        for (int screenIndex = 0; screenIndex < sortedScreens.Length; screenIndex++)
        {
            var screen = sortedScreens[screenIndex];
            bool isPrimary = screen.Primary;

            string screenLabel = isPrimary
                ? $"Screen {screenIndex + 1} (Primary)"
                : $"Screen {screenIndex + 1}";

            var (jpegData, scaleFactor) = CaptureScreenToJpeg(screen.Bounds);
            capturedScreens.Add(new CapturedScreen(jpegData, screenLabel, screen.Bounds, scaleFactor));

            System.Diagnostics.Debug.WriteLine(
                $"📸 Captured {screenLabel}: {screen.Bounds.Width}x{screen.Bounds.Height}, " +
                $"{jpegData.Length / 1024}KB"
            );
        }

        return capturedScreens;
    }

    private static (byte[] JpegData, double ScaleFactor) CaptureScreenToJpeg(Rectangle screenBounds)
    {
        const int MaxWidth = 1280;

        using var bitmap = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);

        graphics.CopyFromScreen(
            screenBounds.Location,
            Point.Empty,
            screenBounds.Size,
            CopyPixelOperation.SourceCopy
        );

        Bitmap outputBitmap = bitmap;
        double scaleFactor = 1.0;

        if (screenBounds.Width > MaxWidth)
        {
            scaleFactor = (double)screenBounds.Width / MaxWidth;
            int newHeight = (int)(screenBounds.Height / scaleFactor);
            outputBitmap = new Bitmap(MaxWidth, newHeight);
            using var resizeGraphics = Graphics.FromImage(outputBitmap);
            resizeGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            resizeGraphics.DrawImage(bitmap, 0, 0, MaxWidth, newHeight);
        }

        using var memoryStream = new MemoryStream();
        var jpegEncoder = GetJpegEncoder();
        var encoderParameters = new EncoderParameters(1);
        encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, 70L);

        outputBitmap.Save(memoryStream, jpegEncoder, encoderParameters);

        if (outputBitmap != bitmap)
            outputBitmap.Dispose();

        return (memoryStream.ToArray(), scaleFactor);
    }

    private static ImageCodecInfo GetJpegEncoder()
    {
        var codecs = ImageCodecInfo.GetImageEncoders();
        return codecs.First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
    }
}
