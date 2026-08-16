using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
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
