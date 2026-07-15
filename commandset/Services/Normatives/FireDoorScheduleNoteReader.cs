using Autodesk.Revit.DB;
using System.Text.RegularExpressions;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Normatives
{
    /// <summary>
    /// Reads «Примечание» cells from door opening-fill schedules (REV-47).
    /// Matches schedule rows to door types by normalized name tokens (e.g. ДОмп 21-12).
    /// </summary>
    public static class FireDoorScheduleNoteReader
    {
        private static readonly string[] ScheduleNameHints =
        {
            "заполнен",
            "дверн",
            "проем",
            "проём",
            "спецификац",
            "door",
            "opening",
        };

        private static readonly string[] NoteHeadingHints =
        {
            "примечан",
            "remark",
            "note",
            "комментар",
        };

        private static readonly string[] NameHeadingHints =
        {
            "наименован",
            "обозначен",
            "тип",
            "type",
            "name",
            "маркиров",
        };

        /// <summary>
        /// Builds a lookup of schedule note text keyed by normalized schedule name cells,
        /// plus raw notes list for fuzzy matching against door family/type.
        /// </summary>
        public static List<ScheduleNoteRow> ReadDoorScheduleNotes(Document doc)
        {
            var rows = new List<ScheduleNoteRow>();
            if (doc == null)
                return rows;

            foreach (var schedule in FindDoorSchedules(doc))
            {
                try
                {
                    rows.AddRange(ReadNotesFromSchedule(schedule));
                }
                catch
                {
                    // Skip unreadable / incomplete schedules.
                }
            }

            return rows;
        }

        /// <summary>
        /// Finds the best matching schedule note for a door family/type name.
        /// </summary>
        public static string FindNoteForDoor(
            IReadOnlyList<ScheduleNoteRow> rows,
            string familyName,
            string typeName)
        {
            if (rows == null || rows.Count == 0)
                return string.Empty;

            var doorKey = NormalizeForMatch($"{familyName} {typeName}");
            if (string.IsNullOrWhiteSpace(doorKey))
                return string.Empty;

            ScheduleNoteRow best = null;
            var bestScore = 0;
            var bestIsFire = false;

            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Note) || IsPlaceholderNote(row.Note))
                    continue;

                var score = ScoreMatch(doorKey, row.NormalizedName);
                if (score < 2)
                    continue;

                var isFire = LooksLikeFireDoorText(row.Note);
                // Prefer fire-door notes over generic notes with the same/lower score.
                if (best == null
                    || score > bestScore
                    || (score == bestScore && isFire && !bestIsFire)
                    || (isFire && !bestIsFire && score >= bestScore - 1))
                {
                    bestScore = score;
                    best = row;
                    bestIsFire = isFire;
                }
            }

            return best != null ? best.Note : string.Empty;
        }

        /// <summary>
        /// Type-catalog / schedule placeholders that are not real remarks.
        /// </summary>
        public static bool IsPlaceholderNote(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return true;

            var trimmed = text.Trim();
            if (trimmed.Length <= 3)
                return true;

            var lower = trimmed.ToLowerInvariant();
            return lower is "<варианты>" or "<variants>" or "<variant>" or "<none>" or "-" or "—"
                   || lower.StartsWith("<вариант")
                   || Regex.IsMatch(lower, @"^<[^>]{1,40}>$");
        }

        public static bool LooksLikeFireDoorText(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || IsPlaceholderNote(text))
                return false;

            var normalized = text.ToLowerInvariant();
            return normalized.Contains("противопожар")
                || normalized.Contains("fire")
                // EI30, EI 30, EI-30
                || Regex.IsMatch(normalized, @"\bei[\s\-]*\d+");
        }

        public static string ClassifyMarkSource(bool fromParameter, bool fromScheduleNote)
        {
            if (fromParameter && fromScheduleNote)
                return "both";
            if (fromParameter)
                return "parameter";
            if (fromScheduleNote)
                return "schedule_note";
            return "none";
        }

        /// <summary>
        /// Normalizes names for fuzzy match: lowercases, strips (prefix), maps 2100-1200 → 21-12.
        /// </summary>
        public static string NormalizeForMatch(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var text = value.ToLowerInvariant();
            text = Regex.Replace(text, @"\([^)]*\)", " ");
            text = text.Replace('_', ' ').Replace('-', ' ');
            text = Regex.Replace(text, @"\s+", " ").Trim();

            // 2100 1200 or 2100x1200 → 21 12 (common door size coding)
            text = Regex.Replace(
                text,
                @"\b(\d{2})\d{2}\b",
                "$1");

            return text;
        }

        internal static int ScoreMatch(string doorKey, string scheduleNameKey)
        {
            if (string.IsNullOrWhiteSpace(doorKey) || string.IsNullOrWhiteSpace(scheduleNameKey))
                return 0;

            var doorTokens = Tokenize(doorKey);
            var scheduleTokens = Tokenize(scheduleNameKey);
            if (doorTokens.Count == 0 || scheduleTokens.Count == 0)
                return 0;

            var score = 0;
            foreach (var token in doorTokens)
            {
                if (token.Length < 2)
                    continue;

                // Skip generic words
                if (IsGenericToken(token))
                    continue;

                if (scheduleTokens.Contains(token))
                    score += token.Length >= 4 ? 2 : 1;
            }

            return score;
        }

        private static HashSet<string> Tokenize(string key)
        {
            return key
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static bool IsGenericToken(string token)
        {
            return token is "дверь"
                or "дверной"
                or "блок"
                or "door"
                or "family"
                or "тип"
                or "type"
                or "л"
                or "п"
                or "лев"
                or "прав";
        }

        private static IEnumerable<ViewSchedule> FindDoorSchedules(Document doc)
        {
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate)
                .ToList();

            foreach (var schedule in schedules)
            {
                if (!IsDoorCategorySchedule(doc, schedule) && !NameLooksLikeDoorSchedule(schedule.Name))
                    continue;

                if (!HasNoteColumn(schedule))
                    continue;

                yield return schedule;
            }
        }

        private static bool IsDoorCategorySchedule(Document doc, ViewSchedule schedule)
        {
            try
            {
                var categoryId = schedule.Definition.CategoryId;
                var category = Category.GetCategory(doc, categoryId);
                if (category == null)
                    return false;

                return category.Id.GetValue() == (long)BuiltInCategory.OST_Doors
                    || (category.Name?.IndexOf("двер", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                    || (category.Name?.IndexOf("door", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool NameLooksLikeDoorSchedule(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var lower = name.ToLowerInvariant();
            var hitCount = ScheduleNameHints.Count(hint => lower.Contains(hint));
            // Prefer schedules about door opening fills (need at least two hints, or «заполнен»)
            return lower.Contains("заполнен") || hitCount >= 2;
        }

        private static bool HasNoteColumn(ViewSchedule schedule)
        {
            return FindColumnIndex(schedule, NoteHeadingHints) >= 0;
        }

        private static List<ScheduleNoteRow> ReadNotesFromSchedule(ViewSchedule schedule)
        {
            var result = new List<ScheduleNoteRow>();
            var noteCol = FindColumnIndex(schedule, NoteHeadingHints);
            if (noteCol < 0)
                return result;

            var nameCol = FindColumnIndex(schedule, NameHeadingHints);
            var tableData = schedule.GetTableData();
            var body = tableData.GetSectionData(SectionType.Body);
            var rowCount = body.NumberOfRows;
            var colCount = body.NumberOfColumns;

            if (noteCol >= colCount)
                return result;

            for (var row = 0; row < rowCount; row++)
            {
                string note;
                string name;
                try
                {
                    note = schedule.GetCellText(SectionType.Body, row, noteCol)?.Trim() ?? string.Empty;
                    name = nameCol >= 0 && nameCol < colCount
                        ? schedule.GetCellText(SectionType.Body, row, nameCol)?.Trim() ?? string.Empty
                        : string.Empty;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(note) || IsPlaceholderNote(note))
                    continue;

                // Skip header-like / empty name rows without fire text unless note alone is useful
                if (string.IsNullOrWhiteSpace(name) && !LooksLikeFireDoorText(note))
                    continue;

                result.Add(new ScheduleNoteRow
                {
                    ScheduleName = schedule.Name,
                    Name = name,
                    Note = note,
                    NormalizedName = NormalizeForMatch(name),
                });
            }

            return result;
        }

        private static int FindColumnIndex(ViewSchedule schedule, string[] headingHints)
        {
            var definition = schedule.Definition;
            var fieldCount = definition.GetFieldCount();
            var bodyColumn = 0;

            for (var i = 0; i < fieldCount; i++)
            {
                var field = definition.GetField(i);
                if (field.IsHidden)
                    continue;

                var heading = $"{field.ColumnHeading} {field.GetName()}".ToLowerInvariant();
                if (headingHints.Any(hint => heading.Contains(hint)))
                    return bodyColumn;

                bodyColumn++;
            }

            return -1;
        }

        public sealed class ScheduleNoteRow
        {
            public string ScheduleName { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Note { get; set; } = string.Empty;
            public string NormalizedName { get; set; } = string.Empty;
        }
    }
}
