using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.DataExtraction;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// The decisions behind <c>check_link_clashes</c> that need no running Revit (REV-167):
    /// what counts as a clash worth reporting, and how a pile of overlaps is read back as
    /// a sentence.
    /// </summary>
    /// <remarks>
    /// Kept apart from the handler so it can be tested for real. Everything here is
    /// arithmetic and strings; the geometry lives in
    /// <c>CheckLinkClashesEventHandler</c> and only means anything against a model.
    /// </remarks>
    public static class ClashRules
    {
        /// <summary>
        /// Below this the two elements are touching, not clashing. 5 mm is the width of a
        /// modelling slip, and a report that lists those is a report nobody reads to the end.
        /// </summary>
        public const double DefaultToleranceMm = 5.0;

        /// <summary>Anything above this hides real clashes rather than noise.</summary>
        public const double MaxToleranceMm = 500.0;

        /// <summary>Category pairs named in the summary. The tail is in the rows.</summary>
        public const int SummaryPairLimit = 8;

        /// <summary>
        /// How close two overlaps of the same link element have to be to count as one
        /// collision. A wall is modelled here as a stack of separate elements — core,
        /// two layers of insulation, plaster, finish — and a beam through it meets all
        /// of them within the wall thickness. 1.5 m is comfortably wider than any such
        /// stack and far narrower than the gap to the next place the same beam bites.
        /// </summary>
        public const double MergeRadiusMm = 1500.0;

        public static double NormaliseToleranceMm(double requested)
        {
            if (double.IsNaN(requested) || requested < 0)
                return DefaultToleranceMm;

            return Math.Min(requested, MaxToleranceMm);
        }

        /// <summary>
        /// Does this overlap go into the report?
        /// </summary>
        /// <remarks>
        /// An unmeasurable overlap (null depth — Revit refused the boolean on messy
        /// geometry) is kept. A clash the tool saw and could not size is still a clash,
        /// and dropping it silently is the one outcome that costs the architect trust:
        /// the row carries a note instead.
        /// </remarks>
        public static bool IsReportable(double? depthMm, double toleranceMm)
        {
            if (depthMm == null)
                return true;

            return depthMm.Value >= toleranceMm;
        }

        /// <summary>
        /// Folds the overlaps of one link element in one place into a single row.
        /// </summary>
        /// <remarks>
        /// A wall here is a stack of separate elements, so one beam through one wall
        /// produced five rows — бетон 500, две минваты, штукатурка, отделка — and four
        /// test beams produced eighteen. On a real КР that multiplies the report by
        /// four or five and buries what matters.
        ///
        /// The deepest overlap becomes the row (it is the structural layer, the one the
        /// argument with the смежник is actually about) and the rest move into
        /// <see cref="LinkClashItem.AlsoHits"/> — nothing is lost, and the layer ids are
        /// still there for whoever has to edit the finish.
        ///
        /// Grouping is by link element AND by place: the same beam biting two different
        /// walls at opposite ends stays two rows, which is what the architect sees when
        /// they look at it.
        /// </remarks>
        public static List<LinkClashItem> MergeStackedHits(
            IEnumerable<LinkClashItem> clashes,
            double radiusMm = MergeRadiusMm)
        {
            var merged = new List<LinkClashItem>();

            foreach (var group in (clashes ?? Enumerable.Empty<LinkClashItem>())
                         .Where(clash => clash != null)
                         .GroupBy(clash => (clash.LinkInstanceId, clash.LinkElementId)))
            {
                // Deepest first, so the row that represents a cluster is picked by the
                // very first member that lands in it. An unmeasured overlap sorts last:
                // it is worth reporting, but it is not the one to name the collision by.
                var ordered = group
                    .OrderByDescending(clash => clash.DepthMm ?? double.MinValue)
                    .ToList();

                var clusters = new List<LinkClashItem>();

                foreach (var clash in ordered)
                {
                    var host = clusters.FirstOrDefault(candidate =>
                        WithinRadius(candidate.PointMm, clash.PointMm, radiusMm));

                    if (host == null)
                    {
                        clusters.Add(clash);
                        continue;
                    }

                    host.AlsoHits ??= new List<ClashLayerHit>();
                    host.AlsoHits.Add(new ClashLayerHit
                    {
                        HostElementId = clash.HostElementId,
                        HostCategory = clash.HostCategory,
                        HostType = clash.HostType,
                        DepthMm = clash.DepthMm
                    });
                }

                merged.AddRange(clusters);
            }

            return merged;
        }

        private static bool WithinRadius(JZPoint a, JZPoint b, double radiusMm)
        {
            if (a == null || b == null)
                return false;

            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;

            return dx * dx + dy * dy + dz * dz <= radiusMm * radiusMm;
        }

        /// <summary>
        /// Folds the clashes by category pair, biggest group first. Computed over every
        /// clash found — the summary describes the model, not the page that came back.
        /// </summary>
        public static List<ClashPairCount> Summarise(IEnumerable<LinkClashItem> clashes, int limit = SummaryPairLimit)
        {
            var groups = new Dictionary<string, ClashPairCount>(StringComparer.CurrentCulture);

            foreach (var clash in clashes ?? Enumerable.Empty<LinkClashItem>())
            {
                if (clash == null)
                    continue;

                var host = clash.HostCategory ?? string.Empty;
                var link = clash.LinkCategory ?? string.Empty;
                var key = host + " ↔ " + link;

                if (!groups.TryGetValue(key, out var pair))
                {
                    pair = new ClashPairCount { HostCategory = host, LinkCategory = link };
                    groups[key] = pair;
                }

                pair.Count++;
                if (clash.DepthMm != null &&
                    (pair.MaxDepthMm == null || clash.DepthMm.Value > pair.MaxDepthMm.Value))
                {
                    pair.MaxDepthMm = Math.Round(clash.DepthMm.Value, 1);
                }
            }

            return groups.Values
                .OrderByDescending(pair => pair.Count)
                .ThenBy(pair => pair.HostCategory, StringComparer.CurrentCulture)
                .ThenBy(pair => pair.LinkCategory, StringComparer.CurrentCulture)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        /// <summary>
        /// The line the architect reads first: how many, in how many links, and which
        /// pairs of categories they are — «Балки ↔ Проёмы — 12», not 300 rows to skim.
        /// </summary>
        public static string BuildMessage(CheckLinkClashesResult result)
        {
            if (result == null)
                return string.Empty;

            var scanned = result.Links?.Count(link => link.Scanned) ?? 0;
            if (scanned == 0)
                return "Не с чем сверять: ни одной загруженной связи не найдено.";

            if (result.TotalClashes == 0)
            {
                var clean = $"Пересечений не найдено. Проверено связей: {scanned}, " +
                            $"элементов модели: {result.HostElementsScanned}, порог {Format(result.ToleranceMm)} мм.";
                if (result.IgnoredBelowTolerance > 0)
                    clean += $" Касаний тоньше порога отброшено: {result.IgnoredBelowTolerance}.";
                return clean;
            }

            var message = $"Пересечений: {result.TotalClashes} в {scanned} " +
                          $"{LinkWord(scanned)}, порог {Format(result.ToleranceMm)} мм.";

            if (result.RawClashCount > result.TotalClashes)
            {
                // Without this line the count looks like the tool missed things: the
                // model shows eighteen overlaps and the report says four.
                message += $" Слои стен свёрнуты: {result.RawClashCount} перекрытий → " +
                           $"{result.TotalClashes} строк, остальные в alsoHits.";
            }

            var top = result.ByCategoryPair ?? new List<ClashPairCount>();
            if (top.Count > 0)
            {
                var named = top
                    .Take(3)
                    .Select(pair => $"{pair.HostCategory} ↔ {pair.LinkCategory} — {pair.Count}");
                message += " Больше всего: " + string.Join("; ", named) + ".";
            }

            if (result.IgnoredBelowTolerance > 0)
                message += $" Касаний тоньше порога отброшено: {result.IgnoredBelowTolerance}.";

            if (result.Truncated)
                message += " Обход остановлен по лимиту — показана часть модели, не вся.";

            return message;
        }

        /// <summary>«связи» / «связях» — the count reads as a sentence, not as a log line.</summary>
        internal static string LinkWord(int count)
        {
            var lastTwo = Math.Abs(count) % 100;
            var last = lastTwo % 10;

            if (lastTwo >= 11 && lastTwo <= 14)
                return "связях";

            return last == 1 ? "связи" : "связях";
        }

        private static string Format(double value) =>
            value == Math.Floor(value)
                ? ((long)value).ToString()
                : Math.Round(value, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }
}
