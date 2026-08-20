namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// The arithmetic behind «задание на отверстия» (REV-168): how big a hole a pipe
    /// needs, when two holes become one, and what the hole is called.
    /// </summary>
    /// <remarks>
    /// Kept free of Revit types so it can be tested for real. Everything here works in
    /// the flat coordinates of the host element — U along the wall (or X in plan for a
    /// floor), V up the wall (or Y in plan) — which is exactly what an opening is drawn
    /// in. Turning a solid into those coordinates is the handler's job.
    /// </remarks>
    public static class MepOpeningRules
    {
        /// <summary>
        /// Free space left around the pipe on each side. 50 mm is what a монтажник needs
        /// to get a sleeve and a seal in; a hole cut exactly to the pipe cannot be built.
        /// </summary>
        public const double DefaultClearanceMm = 50.0;

        /// <summary>
        /// Two holes closer than this become one. Below it the wall is left with a fin of
        /// masonry nobody will build, and a пачка труб gets one honest opening instead of
        /// five holes in a row.
        /// </summary>
        public const double DefaultMergeGapMm = 200.0;

        /// <summary>Opening sizes are rounded up to this step — nobody cuts a 137 mm hole.</summary>
        public const double DefaultSizeStepMm = 50.0;

        /// <summary>An opening smaller than this is not worth a задание.</summary>
        public const double MinOpeningSizeMm = 50.0;

        /// <summary>
        /// A rectangle in the plane of the host element, millimetres. U runs along the
        /// wall (X in plan for a floor), V runs up it (Y in plan).
        /// </summary>
        public struct OpeningRect
        {
            public double MinU;
            public double MaxU;
            public double MinV;
            public double MaxV;

            public OpeningRect(double minU, double maxU, double minV, double maxV)
            {
                MinU = Math.Min(minU, maxU);
                MaxU = Math.Max(minU, maxU);
                MinV = Math.Min(minV, maxV);
                MaxV = Math.Max(minV, maxV);
            }

            public double WidthMm => MaxU - MinU;

            public double HeightMm => MaxV - MinV;

            public double CentreU => (MinU + MaxU) / 2.0;

            public double CentreV => (MinV + MaxV) / 2.0;

            /// <summary>Grows the rectangle by the same amount on all four sides.</summary>
            public OpeningRect Expanded(double byMm) =>
                new OpeningRect(MinU - byMm, MaxU + byMm, MinV - byMm, MaxV + byMm);

            /// <summary>Smallest rectangle covering both.</summary>
            public OpeningRect Union(OpeningRect other) => new OpeningRect(
                Math.Min(MinU, other.MinU),
                Math.Max(MaxU, other.MaxU),
                Math.Min(MinV, other.MinV),
                Math.Max(MaxV, other.MaxV));

            /// <summary>
            /// How far apart the two rectangles are. Zero when they touch or overlap.
            /// Measured per axis and combined, so two holes offset diagonally are only
            /// merged when they are close in both directions.
            /// </summary>
            public double GapTo(OpeningRect other)
            {
                var gapU = Math.Max(0, Math.Max(MinU - other.MaxU, other.MinU - MaxU));
                var gapV = Math.Max(0, Math.Max(MinV - other.MaxV, other.MinV - MaxV));

                if (gapU == 0 && gapV == 0)
                    return 0;

                return Math.Sqrt(gapU * gapU + gapV * gapV);
            }
        }

        /// <summary>
        /// Folds rectangles that sit close together into one. Runs until nothing changes:
        /// a пачка труб merges pair by pair, and merging two also brings in the third that
        /// was only close to the result.
        /// </summary>
        public static List<T> Cluster<T>(
            IReadOnlyList<T> items,
            Func<T, OpeningRect> rectOf,
            Func<T, T, OpeningRect, T> merge,
            double gapMm)
        {
            var result = items?.ToList() ?? new List<T>();
            if (result.Count < 2 || gapMm < 0)
                return result;

            var merged = true;
            while (merged)
            {
                merged = false;

                for (var i = 0; i < result.Count && !merged; i++)
                {
                    for (var j = i + 1; j < result.Count; j++)
                    {
                        var a = rectOf(result[i]);
                        var b = rectOf(result[j]);
                        if (a.GapTo(b) > gapMm)
                            continue;

                        result[i] = merge(result[i], result[j], a.Union(b));
                        result.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// The size that goes on the drawing: the measured rectangle plus clearance,
        /// rounded up to the modular step, never below the minimum.
        /// </summary>
        public static (double WidthMm, double HeightMm) SizeForDrawing(
            OpeningRect measured,
            double clearanceMm,
            double stepMm)
        {
            var grown = measured.Expanded(Math.Max(0, clearanceMm));

            return (
                RoundUpTo(Math.Max(grown.WidthMm, MinOpeningSizeMm), stepMm),
                RoundUpTo(Math.Max(grown.HeightMm, MinOpeningSizeMm), stepMm));
        }

        /// <summary>
        /// Rounds up to a step. A step of zero or less means «as measured» — the caller
        /// asked for the true size and gets it rather than a silent 50 mm default.
        /// </summary>
        public static double RoundUpTo(double valueMm, double stepMm)
        {
            if (stepMm <= 0)
                return Math.Round(valueMm, 1);

            return Math.Ceiling(valueMm / stepMm - 1e-9) * stepMm;
        }

        /// <summary>
        /// «ОТВ-2эт-03» — what goes in the марка and into the ведомость. The level comes
        /// first because that is how a монтажник looks for it on site.
        /// </summary>
        public static string BuildMark(string levelName, int index, string prefix = "ОТВ")
        {
            var level = string.IsNullOrWhiteSpace(levelName) ? null : Compact(levelName);
            var number = index.ToString("00");

            return level == null ? $"{prefix}-{number}" : $"{prefix}-{level}-{number}";
        }

        /// <summary>«2 этаж» → «2эт»: a mark has to fit in a tag, not in a sentence.</summary>
        internal static string Compact(string levelName)
        {
            var trimmed = levelName.Trim();
            var parts = trimmed.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 2 && parts[1].StartsWith("эт", StringComparison.CurrentCultureIgnoreCase))
                return parts[0] + "эт";

            return trimmed.Replace(" ", string.Empty);
        }
    }
}
