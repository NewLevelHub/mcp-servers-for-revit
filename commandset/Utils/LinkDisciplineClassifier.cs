using System.Text.RegularExpressions;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Раздел проекта by the link's file name — АР / КР / ИОС / ГП (REV-166).
    /// </summary>
    /// <remarks>
    /// The file name is the only thing every office actually fills in. Revit has no
    /// «раздел» field: <c>RevitLinkType</c> knows a path and a status, nothing else,
    /// and the linked document's project information is filled in on maybe half the
    /// files that come from a subcontractor.
    ///
    /// So the section is a reading of the name, and the reading is reported with the
    /// token it came from (<see cref="Discipline.MatchedToken"/>). An architect who
    /// sees «ИОС — по «ОВ» в имени файла» can tell at a glance that the guess is
    /// right; a bare «ИОС» would have to be trusted blind.
    /// </remarks>
    public static class LinkDisciplineClassifier
    {
        /// <summary>What the file name says the link is, and which word said it.</summary>
        public sealed class Discipline
        {
            /// <summary>АР | КР | ИОС | ГП, or empty when the name gives nothing away.</summary>
            public string Section { get; set; } = string.Empty;

            /// <summary>ОВ / ВК / ЭОМ / СС / ГС / ТС inside ИОС. Empty for the rest.</summary>
            public string Subsection { get; set; } = string.Empty;

            /// <summary>The token in the file name the reading came from.</summary>
            public string MatchedToken { get; set; } = string.Empty;

            public bool IsKnown => !string.IsNullOrEmpty(Section);

            /// <summary>«ИОС / ОВ» or «АР» — what goes into the report.</summary>
            public string Display =>
                !IsKnown ? string.Empty :
                string.IsNullOrEmpty(Subsection) ? Section : $"{Section} / {Subsection}";
        }

        /// <summary>
        /// Марки разделов по ГОСТ Р 21.101, plus the Latin spellings that show up in
        /// files exchanged with foreign teams. Deliberately narrow: «ПС» and «ОС» are
        /// left out because they are also ordinary word fragments, and a wrong section
        /// on a link list is worse than an honest «раздел не определён».
        /// </summary>
        private static readonly Dictionary<string, (string Section, string Subsection)> Marks =
            new Dictionary<string, (string, string)>(StringComparer.Ordinal)
            {
                ["АР"] = ("АР", ""),
                ["АС"] = ("АР", ""),
                ["АРХИТЕКТУРА"] = ("АР", ""),
                ["АРХИТЕКТУРНЫЕ"] = ("АР", ""),
                ["ARCH"] = ("АР", ""),

                ["КР"] = ("КР", ""),
                ["КЖ"] = ("КР", ""),
                ["КМ"] = ("КР", ""),
                ["КМД"] = ("КР", ""),
                ["КОНСТРУКЦИИ"] = ("КР", ""),
                ["КОНСТРУКТИВ"] = ("КР", ""),
                ["STRUCT"] = ("КР", ""),

                ["ИОС"] = ("ИОС", ""),
                ["MEP"] = ("ИОС", ""),
                ["ОВ"] = ("ИОС", "ОВ"),
                ["ОВИК"] = ("ИОС", "ОВ"),
                ["ОВК"] = ("ИОС", "ОВ"),
                ["ХС"] = ("ИОС", "ОВ"),
                ["ОТОПЛЕНИЕ"] = ("ИОС", "ОВ"),
                ["ВЕНТИЛЯЦИЯ"] = ("ИОС", "ОВ"),
                ["HVAC"] = ("ИОС", "ОВ"),
                ["ВК"] = ("ИОС", "ВК"),
                ["НВК"] = ("ИОС", "ВК"),
                ["ВОДОСНАБЖЕНИЕ"] = ("ИОС", "ВК"),
                ["КАНАЛИЗАЦИЯ"] = ("ИОС", "ВК"),
                ["ЭОМ"] = ("ИОС", "ЭОМ"),
                ["ЭМ"] = ("ИОС", "ЭОМ"),
                ["ЭС"] = ("ИОС", "ЭОМ"),
                ["ЭЛЕКТРИКА"] = ("ИОС", "ЭОМ"),
                ["ЭЛЕКТРОСНАБЖЕНИЕ"] = ("ИОС", "ЭОМ"),
                ["СС"] = ("ИОС", "СС"),
                ["СКС"] = ("ИОС", "СС"),
                ["АПС"] = ("ИОС", "СС"),
                ["АУПТ"] = ("ИОС", "СС"),
                ["СОУЭ"] = ("ИОС", "СС"),
                ["ГС"] = ("ИОС", "ГС"),
                ["ГСВ"] = ("ИОС", "ГС"),
                ["ТС"] = ("ИОС", "ТС"),
                ["ТМ"] = ("ИОС", "ТС"),
                ["ИТП"] = ("ИОС", "ТС"),

                ["ГП"] = ("ГП", ""),
                ["ПЗУ"] = ("ГП", ""),
                ["ГЕНПЛАН"] = ("ГП", ""),
            };

        /// <summary>
        /// Reads the section off a link file name. Never throws and never guesses past
        /// the table above — an unknown name comes back with an empty section.
        /// </summary>
        public static Discipline Classify(string fileName)
        {
            var result = new Discipline();
            if (string.IsNullOrWhiteSpace(fileName))
                return result;

            foreach (var token in Tokenize(fileName))
            {
                if (!Marks.TryGetValue(token, out var mark))
                    continue;

                if (!result.IsKnown)
                {
                    result.Section = mark.Section;
                    result.Subsection = mark.Subsection;
                    result.MatchedToken = token;
                    if (!string.IsNullOrEmpty(result.Subsection))
                        return result;
                    continue;
                }

                // «ИОС4.1_ОВ» names the section first and the trade after it. The
                // first token already fixed the section; only a later token of the
                // same section may fill in the trade.
                if (mark.Section == result.Section && !string.IsNullOrEmpty(mark.Subsection))
                {
                    result.Subsection = mark.Subsection;
                    result.MatchedToken = token;
                    return result;
                }
            }

            return result;
        }

        /// <summary>
        /// Letter/digit runs of the file name, upper-cased, each also yielded with its
        /// trailing digits stripped: «ИОС4» is «ИОС», «АР_2этап» keeps «АР».
        /// </summary>
        /// <remarks>
        /// Only the last path segment is read: a file sitting in an «ОВ» folder is not
        /// an ОВ model by that alone. The split is done by hand rather than through
        /// <c>Path</c>, because a link name as Revit words it («Корпус.rvt : 1 : location»)
        /// carries characters the .NET Framework path helpers reject outright.
        /// </remarks>
        internal static IEnumerable<string> Tokenize(string fileName)
        {
            var name = (fileName ?? string.Empty).Split('\\', '/').Last();

            foreach (Match match in Regex.Matches(name, @"[\p{L}\p{Nd}]+"))
            {
                var token = match.Value.ToUpperInvariant();
                yield return token;

                var trimmed = token.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                if (trimmed.Length > 0 && trimmed.Length != token.Length)
                    yield return trimmed;
            }
        }
    }
}
