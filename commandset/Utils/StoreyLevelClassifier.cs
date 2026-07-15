using System.Text.RegularExpressions;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Classifies Revit levels for TEP storey count (REV-42).
    /// Main этажность = above-ground only; basement / technical / roof are separate.
    /// </summary>
    public static class StoreyLevelClassifier
    {
        public const string AboveGround = "aboveGround";
        public const string Basement = "basement";
        public const string Technical = "technical";
        public const string Roof = "roof";

        // −1 этаж, -2, –1 (unicode dashes)
        private static readonly Regex NegativeStoreyName = new Regex(
            @"^[-\u2212\u2013]\s*\d",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // 1 этаж, 16 этаж, Уровень 2, Level 3, Ур. 1
        private static readonly Regex NumberedAboveGround = new Regex(
            @"^(?:(?:уровень|level|ур\.?)\s*\d+|\d+\s*(?:этаж|level|ур\.?\b))",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // Тех.этаж, тех этаж, техэтаж, технический, MEP, инженерный этаж
        private static readonly Regex TechnicalLevel = new Regex(
            @"тех\.?\s*эт|техэтаж|техническ|technical|\bmep\b|мэп|инженерн\w*\s*эт",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // Кровля, крыша, чердак, attic, roof
        private static readonly Regex RoofLevel = new Regex(
            @"кровл|крыш|чердак|attic|\broof\b",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // Цоколь, подвал, паркинг, основание, фунд. …
        private static readonly Regex BasementByName = new Regex(
            @"цоколь|подвал|подземн|basement|parking|паркинг|автостоян|основани|фунд",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// Classify a level by its name and elevation in mm.
        /// Priority: roof → technical → basement (name/−N) → numbered above-ground → elevation → aboveGround.
        /// </summary>
        public static string Classify(string levelName, double elevationMm)
        {
            var normalized = Normalize(levelName);

            if (RoofLevel.IsMatch(normalized))
                return Roof;

            if (TechnicalLevel.IsMatch(normalized))
                return Technical;

            if (NegativeStoreyName.IsMatch(normalized) || BasementByName.IsMatch(normalized))
                return Basement;

            // "1 этаж" / "Уровень 2" stay above-ground even if elevation is slightly off.
            if (NumberedAboveGround.IsMatch(normalized))
                return AboveGround;

            if (elevationMm < -1.0)
                return Basement;

            return AboveGround;
        }

        public static bool IsAboveGround(string levelName, double elevationMm) =>
            Classify(levelName, elevationMm) == AboveGround;

        private static string Normalize(string levelName)
        {
            if (string.IsNullOrWhiteSpace(levelName))
                return string.Empty;

            return levelName
                .Trim()
                .ToLowerInvariant()
                .Replace('ё', 'е');
        }
    }
}
