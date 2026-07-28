using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Pull plain text from common office/text attachments so any OpenAI-compatible
    /// chat endpoint can read them (many proxies do not support Chat Completions <c>file</c> parts).
    /// </summary>
    internal static class DocumentTextExtractor
    {
        public const int MaxExtractedChars = 60000;
        public const int MaxExtractedCharsTotal = 48000;

        public static bool TryExtract(ChatAttachment attachment, out string text, out string error)
        {
            return TryExtract(attachment, MaxExtractedChars, out text, out error);
        }

        public static bool TryExtract(ChatAttachment attachment, int maxChars, out string text, out string error)
        {
            text = null;
            error = null;
            if (attachment?.Data == null || attachment.Data.Length == 0)
            {
                error = "пустой файл";
                return false;
            }

            if (maxChars < 2000)
                maxChars = 2000;
            if (maxChars > MaxExtractedChars)
                maxChars = MaxExtractedChars;

            var ext = Path.GetExtension(attachment.FileName ?? "")?.ToLowerInvariant() ?? "";
            try
            {
                switch (ext)
                {
                    case ".txt":
                    case ".md":
                    case ".markdown":
                    case ".log":
                    case ".csv":
                    case ".tsv":
                    case ".json":
                    case ".xml":
                    case ".html":
                    case ".htm":
                        text = DecodeTextBytes(attachment.Data);
                        break;
                    case ".docx":
                        text = ExtractDocx(attachment.Data);
                        break;
                    case ".xlsx":
                        text = ExtractXlsx(attachment.Data);
                        break;
                    case ".pptx":
                        text = ExtractPptx(attachment.Data);
                        break;
                    case ".rtf":
                        text = ExtractRtfRough(DecodeTextBytes(attachment.Data));
                        break;
                    case ".doc":
                    case ".xls":
                    case ".ppt":
                        error = "старый формат ." + ext.TrimStart('.') +
                                " — сохраните как ." + ext.TrimStart('.') + "x (Office 2007+)";
                        return false;
                    case ".pdf":
                        // Keep native file part for vision-capable OpenAI; no local PDF parser here.
                        error = null;
                        return false;
                    default:
                        error = "нет локального разбора для " + ext;
                        return false;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    error = "в файле не найден текст";
                    return false;
                }

                text = Truncate(text.Trim(), maxChars);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string Truncate(string s, int maxChars)
        {
            if (s.Length <= maxChars)
                return s;
            return s.Substring(0, maxChars) + "\n\n…[текст обрезан, файл длинный]";
        }

        private static string DecodeTextBytes(byte[] data)
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                return Encoding.UTF8.GetString(data, 3, data.Length - 3);
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
                return Encoding.Unicode.GetString(data, 2, data.Length - 2);
            if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            return Encoding.UTF8.GetString(data);
        }

        private static string ExtractDocx(byte[] data)
        {
            using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
            {
                var entry = zip.GetEntry("word/document.xml");
                if (entry == null)
                    throw new InvalidOperationException("в docx нет word/document.xml");

                using (var stream = entry.Open())
                {
                    var doc = XDocument.Load(stream);
                    XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                    var sb = new StringBuilder();
                    foreach (var p in doc.Descendants(w + "p"))
                    {
                        var line = string.Concat(p.Descendants(w + "t").Select(t => (string)t));
                        sb.AppendLine(line);
                    }
                    return sb.ToString();
                }
            }
        }

        private static string ExtractXlsx(byte[] data)
        {
            using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
            {
                var shared = ReadSharedStrings(zip);
                var sb = new StringBuilder();
                var sheets = zip.Entries
                    .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                                && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName)
                    .Take(5);

                foreach (var sheet in sheets)
                {
                    sb.AppendLine("--- " + Path.GetFileNameWithoutExtension(sheet.Name) + " ---");
                    using (var stream = sheet.Open())
                    {
                        var doc = XDocument.Load(stream);
                        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                        foreach (var row in doc.Descendants(main + "row").Take(200))
                        {
                            var cells = new List<string>();
                            foreach (var c in row.Elements(main + "c"))
                            {
                                cells.Add(ReadCell(c, shared, main));
                            }
                            if (cells.Any(x => !string.IsNullOrWhiteSpace(x)))
                                sb.AppendLine(string.Join("\t", cells));
                        }
                    }
                    sb.AppendLine();
                }

                return sb.ToString();
            }
        }

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            var entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry == null)
                return list;

            using (var stream = entry.Open())
            {
                var doc = XDocument.Load(stream);
                XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                foreach (var si in doc.Descendants(main + "si"))
                {
                    var text = string.Concat(si.Descendants(main + "t").Select(t => (string)t));
                    list.Add(text ?? "");
                }
            }
            return list;
        }

        private static string ReadCell(XElement cell, List<string> shared, XNamespace main)
        {
            var type = (string)cell.Attribute("t");
            var v = cell.Element(main + "v")?.Value ?? "";
            if (type == "s" && int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count)
                return shared[idx];
            if (type == "inlineStr")
                return string.Concat(cell.Descendants(main + "t").Select(t => (string)t));
            return v;
        }

        private static string ExtractPptx(byte[] data)
        {
            using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
            {
                var sb = new StringBuilder();
                var slides = zip.Entries
                    .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                                && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName)
                    .Take(30);

                var n = 0;
                foreach (var slide in slides)
                {
                    n++;
                    sb.AppendLine("--- Слайд " + n + " ---");
                    using (var stream = slide.Open())
                    {
                        var doc = XDocument.Load(stream);
                        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
                        foreach (var t in doc.Descendants(a + "t"))
                        {
                            var s = (string)t;
                            if (!string.IsNullOrWhiteSpace(s))
                                sb.AppendLine(s);
                        }
                    }
                    sb.AppendLine();
                }
                return sb.ToString();
            }
        }

        private static string ExtractRtfRough(string rtf)
        {
            if (string.IsNullOrEmpty(rtf))
                return "";
            // Strip simple RTF control words; good enough for short notes.
            var noGroups = Regex.Replace(rtf, @"\{\\.*?\}", " ");
            var noControls = Regex.Replace(noGroups, @"\\[a-zA-Z]+-?\d* ?", " ");
            return noControls.Replace("{", " ").Replace("}", " ");
        }
    }
}
