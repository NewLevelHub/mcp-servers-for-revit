using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Grabs a picture of the Revit window at the moment the architect complains.
    /// A dislike without one is a sentence about a drawing nobody else can see —
    /// "не то проставил" means nothing until you look at the sheet.
    /// </summary>
    public static class FeedbackScreenshot
    {
        /// <summary>Sub-folder of Logs/ where shots live; the export package copies it verbatim.</summary>
        public const string ShotsFolderName = "feedback-shots";

        /// <summary>Revit on a 4K monitor produces ~8 MB per frame; the report has to travel.</summary>
        private const int MaxWidth = 1600;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        public static string GetShotsDirectory()
        {
            var dir = Path.Combine(PathManager.GetLogsDirectoryPath(), ShotsFolderName);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Captures the Revit main window (whole virtual screen when it can't be located)
        /// and returns the saved PNG path, or null when the grab failed. Never throws —
        /// a screenshot is a bonus, losing it must not lose the complaint itself.
        /// </summary>
        public static string Capture(string turnId)
        {
            try
            {
                Rectangle bounds = ResolveBounds();
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    return null;

                using (var raw = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(raw))
                    {
                        g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                    }

                    var path = Path.Combine(GetShotsDirectory(), BuildFileName(turnId));
                    using (var shrunk = Downscale(raw))
                    {
                        (shrunk ?? raw).Save(path, ImageFormat.Png);
                    }
                    return path;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// True when the clipboard holds a picture we could attach — a Win+Shift+S crop,
        /// a copied image file, anything with pixels in it.
        /// </summary>
        public static bool ClipboardHasImage()
        {
            try
            {
                return System.Windows.Clipboard.ContainsImage() || FirstImageFromDropList() != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Saves the clipboard picture beside the captured ones and returns its PNG path,
        /// or null when the clipboard holds no image. The full-screen grab shows the whole
        /// window; the architect's own Win+Shift+S crop shows the one wall that came out
        /// wrong — so let them choose. Never throws, same contract as <see cref="Capture"/>.
        /// </summary>
        public static string SaveFromClipboard(string turnId)
        {
            try
            {
                var image = ReadClipboardImage();
                if (image == null)
                    return null;

                var path = Path.Combine(GetShotsDirectory(), BuildFileName(turnId));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(Downscale(image)));
                using (var file = File.Create(path))
                {
                    encoder.Save(file);
                }
                return path;
            }
            catch
            {
                return null;
            }
        }

        private static BitmapSource ReadClipboardImage()
        {
            // A Snipping Tool crop also lands on the clipboard as a real PNG stream, and that
            // copy is the one worth taking: Clipboard.GetImage rebuilds the picture from the
            // DIB, where an all-zero alpha channel is indistinguishable from an opaque one,
            // and the crop then saves out as solid black.
            try
            {
                var data = System.Windows.Clipboard.GetDataObject();
                if (data != null && data.GetDataPresent("PNG"))
                {
                    using (var stream = data.GetData("PNG") as Stream)
                    {
                        if (stream != null)
                            return Freeze(BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad));
                    }
                }
            }
            catch { /* fall through to the plain bitmap */ }

            try
            {
                if (System.Windows.Clipboard.ContainsImage())
                {
                    var image = System.Windows.Clipboard.GetImage();
                    if (image != null)
                        return Freeze(new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgr24, null, 0));
                }
            }
            catch { /* fall through to a copied file */ }

            try
            {
                var file = FirstImageFromDropList();
                if (file != null)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad; // do not hold the file open
                    bmp.UriSource = new Uri(file);
                    bmp.EndInit();
                    return Freeze(bmp);
                }
            }
            catch { /* nothing usable on the clipboard */ }

            return null;
        }

        /// <summary>
        /// First clipboard entry that looks like an image file — copying a picture in Explorer
        /// puts a path list on the clipboard, not pixels.
        /// </summary>
        private static string FirstImageFromDropList()
        {
            System.Collections.Specialized.StringCollection files;
            try
            {
                if (!System.Windows.Clipboard.ContainsFileDropList())
                    return null;
                files = System.Windows.Clipboard.GetFileDropList();
            }
            catch
            {
                return null;
            }

            if (files == null)
                return null;

            foreach (string file in files)
            {
                if (string.IsNullOrEmpty(file))
                    continue;
                var ext = Path.GetExtension(file);
                if (string.IsNullOrEmpty(ext))
                    continue;
                if (Array.IndexOf(ClipboardImageExtensions, ext.ToLowerInvariant()) >= 0 && File.Exists(file))
                    return file;
            }
            return null;
        }

        private static readonly string[] ClipboardImageExtensions =
            { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

        /// <summary>Same width cap as a captured shot — the report still has to travel.</summary>
        private static BitmapSource Downscale(BitmapSource source)
        {
            if (source.PixelWidth <= MaxWidth)
                return source;

            var scale = (double)MaxWidth / source.PixelWidth;
            return Freeze(new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale)));
        }

        private static BitmapSource Freeze(BitmapSource source)
        {
            if (source != null && source.CanFreeze && !source.IsFrozen)
                source.Freeze();
            return source;
        }

        private static Rectangle ResolveBounds()
        {
            try
            {
                var hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (hwnd != IntPtr.Zero && !IsIconic(hwnd) && GetWindowRect(hwnd, out var r))
                {
                    var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    if (rect.Width > 0 && rect.Height > 0)
                        return rect;
                }
            }
            catch { /* fall through to the whole desktop */ }

            return System.Windows.Forms.SystemInformation.VirtualScreen;
        }

        /// <summary>Returns null when the source already fits, so the caller saves the original.</summary>
        private static Bitmap Downscale(Bitmap source)
        {
            if (source.Width <= MaxWidth)
                return null;

            var scale = (double)MaxWidth / source.Width;
            var width = MaxWidth;
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));

            var target = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            try
            {
                using (var g = Graphics.FromImage(target))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.DrawImage(source, 0, 0, width, height);
                }
                return target;
            }
            catch
            {
                target.Dispose();
                throw;
            }
        }

        private static string BuildFileName(string turnId)
        {
            var safe = SanitizeForFileName(turnId);
            if (string.IsNullOrEmpty(safe)) safe = "turn";
            if (safe.Length > 40) safe = safe.Substring(0, 40);
            return $"{safe}_{DateTime.Now:yyyyMMdd-HHmmss}.png";
        }

        private static string SanitizeForFileName(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                    chars[i] = '-';
            }
            return new string(chars);
        }
    }
}
