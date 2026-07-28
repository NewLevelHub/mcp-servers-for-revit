using System;
using System.Collections.Generic;
using System.IO;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// User-supplied attachment for a multimodal chat turn (OpenAI Chat Completions).
    /// Images → vision; PDF/Office/text → file parts (API extracts text / spreadsheet preview).
    /// </summary>
    public sealed class ChatAttachment
    {
        public const int MaxAttachmentsPerMessage = 5;
        public const int MaxBytesPerFile = 8 * 1024 * 1024;
        public const int MaxTotalBytes = 20 * 1024 * 1024;

        public string FileName { get; set; }
        public string MimeType { get; set; }
        public byte[] Data { get; set; }

        public bool IsImage =>
            !string.IsNullOrEmpty(MimeType) &&
            MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

        public bool IsPdf =>
            string.Equals(MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase);

        /// <summary>Non-image file sent as OpenAI <c>file</c> content part.</summary>
        public bool IsDocument => !IsImage && !string.IsNullOrEmpty(MimeType);

        public string KindLabel
        {
            get
            {
                var ext = Path.GetExtension(FileName ?? "")?.ToLowerInvariant() ?? "";
                switch (ext)
                {
                    case ".pdf": return "PDF";
                    case ".doc":
                    case ".docx":
                    case ".dot":
                    case ".rtf":
                    case ".odt": return "Word";
                    case ".xls":
                    case ".xlsx":
                    case ".csv":
                    case ".tsv": return "Excel";
                    case ".ppt":
                    case ".pptx": return "PPT";
                    case ".txt":
                    case ".md":
                    case ".markdown":
                    case ".log": return "Текст";
                    case ".json":
                    case ".xml":
                    case ".html":
                    case ".htm": return "Данные";
                    default:
                        return IsImage ? "Фото" : "Файл";
                }
            }
        }

        public string DisplayLabel
        {
            get
            {
                var name = string.IsNullOrWhiteSpace(FileName) ? "файл" : FileName;
                if (Data == null || Data.Length == 0)
                    return KindLabel + " · " + name;
                var kb = Math.Max(1, Data.Length / 1024);
                var size = kb >= 1024
                    ? $"{kb / 1024.0:0.#} МБ"
                    : $"{kb} КБ";
                return $"{KindLabel} · {name} ({size})";
            }
        }

        public string ToDataUrl()
        {
            if (Data == null || Data.Length == 0)
                throw new InvalidOperationException("Пустое вложение.");
            var mime = string.IsNullOrWhiteSpace(MimeType) ? "application/octet-stream" : MimeType;
            return "data:" + mime + ";base64," + Convert.ToBase64String(Data);
        }

        public static string GuessMimeType(string fileName)
        {
            var ext = Path.GetExtension(fileName ?? "")?.ToLowerInvariant() ?? "";
            switch (ext)
            {
                // Images
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";

                // PDF
                case ".pdf": return "application/pdf";

                // Word / rich text
                case ".doc": return "application/msword";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".dot": return "application/msword";
                case ".rtf": return "application/rtf";
                case ".odt": return "application/vnd.oasis.opendocument.text";

                // Excel / sheets
                case ".xls": return "application/vnd.ms-excel";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".csv": return "text/csv";
                case ".tsv": return "text/tab-separated-values";

                // PowerPoint
                case ".ppt": return "application/vnd.ms-powerpoint";
                case ".pptx": return "application/vnd.openxmlformats-officedocument.presentationml.presentation";

                // Text / data
                case ".txt":
                case ".log": return "text/plain";
                case ".md":
                case ".markdown": return "text/markdown";
                case ".json": return "application/json";
                case ".xml": return "text/xml";
                case ".html":
                case ".htm": return "text/html";

                default: return null;
            }
        }

        public static bool IsSupportedPath(string path)
        {
            return GuessMimeType(path) != null;
        }

        public static string SupportedTypesHint =>
            "изображения, PDF, Word, Excel, PowerPoint, txt/csv/json/md";

        public static ChatAttachment FromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Файл не найден.", path);

            var mime = GuessMimeType(path);
            if (mime == null)
                throw new NotSupportedException("Формат не поддерживается. Можно: " + SupportedTypesHint + ".");

            var info = new FileInfo(path);
            if (info.Length > MaxBytesPerFile)
                throw new InvalidOperationException(
                    $"Файл слишком большой (макс. {MaxBytesPerFile / (1024 * 1024)} МБ): {Path.GetFileName(path)}");

            return new ChatAttachment
            {
                FileName = Path.GetFileName(path),
                MimeType = mime,
                Data = File.ReadAllBytes(path)
            };
        }

        public static ChatAttachment FromBytes(string fileName, string mimeType, byte[] data)
        {
            if (data == null || data.Length == 0)
                throw new InvalidOperationException("Пустой файл.");
            if (data.Length > MaxBytesPerFile)
                throw new InvalidOperationException(
                    $"Файл слишком большой (макс. {MaxBytesPerFile / (1024 * 1024)} МБ).");

            return new ChatAttachment
            {
                FileName = string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName,
                MimeType = mimeType,
                Data = data
            };
        }

        public static string ValidateBatch(IList<ChatAttachment> existing, ChatAttachment next)
        {
            if (next == null)
                return "Пустое вложение.";
            if (existing != null && existing.Count >= MaxAttachmentsPerMessage)
                return $"Не больше {MaxAttachmentsPerMessage} файлов за раз.";

            long total = next.Data?.Length ?? 0;
            if (existing != null)
            {
                foreach (var a in existing)
                    total += a?.Data?.Length ?? 0;
            }

            if (total > MaxTotalBytes)
                return $"Суммарный размер вложений больше {MaxTotalBytes / (1024 * 1024)} МБ.";

            if (!next.IsImage && !next.IsDocument)
                return "Формат не поддерживается. Можно: " + SupportedTypesHint + ".";

            return null;
        }
    }
}
