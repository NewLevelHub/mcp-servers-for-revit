using RevitMCPCommandSet.Models.Architecture;
using RevitMCPCommandSet.Models.Common;

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
        /// How much of the host a run has to get through before it needs a hole rather
        /// than an argument. A pipe passing through enters one face and leaves by the
        /// other, so the overlap spans the whole thickness; a beam lying along the inside
        /// of a wall overlaps it by a millimetre and needs no opening at all — it needs
        /// the смежник to move it. 0.8 rather than 1.0 because a run crossing at an angle
        /// near the end of a wall can clip a corner and still be a genuine crossing.
        /// </summary>
        public const double ThroughFraction = 0.8;

        /// <summary>
        /// How square-on a run has to meet an element before it is crossing it rather
        /// than running along it: the cosine between the axis of the run and the normal
        /// of the host.
        /// </summary>
        /// <remarks>
        /// <see cref="PassesThrough"/> alone cannot tell the two apart on a thin layer.
        /// A beam lying along a 15 mm finish covers the whole of those 15 mm, so by depth
        /// it «passes through» — and the live run duly asked for a 4250 mm hole through
        /// the отделка. By direction it is unmistakable: a crossing has a real component
        /// along the normal, a run lying against the face has almost none.
        ///
        /// 0.2 is about 78° off square — generous, because a pipe threading a wall at a
        /// sharp angle is still a pipe that needs a hole.
        /// </remarks>
        public const double MinCrossingAlignment = 0.2;

        /// <summary>
        /// Is this run crossing the element, or travelling along it?
        /// </summary>
        /// <param name="alignmentWithNormal">
        /// |cos| between the axis of the run and the normal of the host. Negative values
        /// are treated as their absolute value — direction along the axis is meaningless
        /// here. Pass a negative number for «unknown» to keep the opening.
        /// </param>
        public static bool RunCrossesHost(
            double alignmentWithNormal,
            double threshold = MinCrossingAlignment)
        {
            if (double.IsNaN(alignmentWithNormal))
                return true;

            return Math.Abs(alignmentWithNormal) >= threshold;
        }

        /// <summary>
        /// How far apart the layers of one wall assembly can sit. Openings for the same
        /// run within this distance are one hole through a stack — бетон, утеплитель,
        /// штукатурка, отделка — not several holes in several walls.
        /// </summary>
        public const double LayerRadiusMm = 800.0;

        /// <summary>
        /// Does this run pass through, or merely graze the inside of the element?
        /// </summary>
        /// <remarks>
        /// A thickness we could not measure counts as passing through: dropping a real
        /// opening because the host was hard to read is the expensive way to be wrong,
        /// and the row can still be judged by eye in the preview.
        /// </remarks>
        public static bool PassesThrough(
            double overlapThroughMm,
            double hostThicknessMm,
            double fraction = ThroughFraction)
        {
            if (hostThicknessMm <= 0)
                return true;

            return overlapThroughMm >= fraction * hostThicknessMm;
        }

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
        /// Folds the openings cut for the same run through the layers of one wall into a
        /// single row of the задание.
        /// </summary>
        /// <remarks>
        /// A wall is a stack of separate elements here, so one pipe through one wall asked
        /// for бетон, two layers of минвата, штукатурка and отделка — five holes. As a
        /// document that is wrong: it is one hole. As an instruction to Revit it is right:
        /// every layer is its own element and every one has to be cut, or the pipe runs
        /// into the insulation.
        ///
        /// So the thickest layer — the structural one, the size the задание is really
        /// about — becomes the row, and the rest move into <see cref="MepOpeningPlanItem.AlsoCuts"/>
        /// with their own ids and centres. All of them are then cut at the size of the row,
        /// so the hole is the same rectangle all the way through the assembly.
        ///
        /// Grouping is by run AND by place: the same pipe through two walls a room apart
        /// stays two openings.
        /// </remarks>
        public static List<MepOpeningPlanItem> FoldLayers(
            IEnumerable<MepOpeningPlanItem> items,
            double radiusMm = LayerRadiusMm)
        {
            var folded = new List<MepOpeningPlanItem>();

            // Thickest first, so the layer that becomes the row is the first one to open
            // a group rather than whichever the collector happened to return first.
            var ordered = (items ?? Enumerable.Empty<MepOpeningPlanItem>())
                .Where(item => item != null)
                .OrderByDescending(item => item.HostThicknessMm)
                .ToList();

            foreach (var item in ordered)
            {
                var primary = folded.FirstOrDefault(candidate =>
                    SharesARun(candidate, item) &&
                    WithinRadius(candidate.CentreMm, item.CentreMm, radiusMm));

                if (primary == null)
                {
                    folded.Add(item);
                    continue;
                }

                primary.AlsoCuts ??= new List<MepOpeningLayerCut>();
                primary.AlsoCuts.Add(new MepOpeningLayerCut
                {
                    HostElementId = item.HostElementId,
                    HostCategory = item.HostCategory,
                    HostType = item.HostType,
                    HostThicknessMm = item.HostThicknessMm,
                    CentreMm = item.CentreMm
                });

                // The hole has to clear the widest reading of it anywhere in the stack.
                primary.WidthMm = Math.Max(primary.WidthMm, item.WidthMm);
                primary.HeightMm = Math.Max(primary.HeightMm, item.HeightMm);
            }

            return folded;
        }

        /// <summary>Two openings are for the same hole only if they are for the same run.</summary>
        private static bool SharesARun(MepOpeningPlanItem a, MepOpeningPlanItem b)
        {
            if (a?.MepElementIds == null || b?.MepElementIds == null)
                return false;

            return a.MepElementIds.Intersect(b.MepElementIds).Any();
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
