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
    public record CapturedScreen(byte[] JpegData, string Label, Rectangle Bounds);

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

            byte[] jpegData = CaptureScreenToJpeg(screen.Bounds);
            capturedScreens.Add(new CapturedScreen(jpegData, screenLabel, screen.Bounds));

            System.Diagnostics.Debug.WriteLine(
                $"📸 Captured {screenLabel}: {screen.Bounds.Width}x{screen.Bounds.Height}, " +
                $"{jpegData.Length / 1024}KB"
            );
        }

        return capturedScreens;
    }

    private static byte[] CaptureScreenToJpeg(Rectangle screenBounds)
    {
        using var bitmap = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);

        // CopyFromScreen captures the screen pixels into our bitmap
        graphics.CopyFromScreen(
            screenBounds.Location,
            Point.Empty,
            screenBounds.Size,
            CopyPixelOperation.SourceCopy
        );

        using var memoryStream = new MemoryStream();

        // JPEG at quality 85 — good enough for Claude's vision API, smaller than PNG
        var jpegEncoder = GetJpegEncoder();
        var encoderParameters = new EncoderParameters(1);
        encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, 85L);

        bitmap.Save(memoryStream, jpegEncoder, encoderParameters);
        return memoryStream.ToArray();
    }

    private static ImageCodecInfo GetJpegEncoder()
    {
        var codecs = ImageCodecInfo.GetImageEncoders();
        return codecs.First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
    }
}
