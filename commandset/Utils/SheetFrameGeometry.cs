using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Printable area of a sheet in sheet coordinates (feet).
    ///
    /// <para><see cref="ViewSheet.Outline"/> is NOT the paper: for ADSK title blocks it
    /// starts at the sheet origin and grows to cover whatever was placed, so a table that
    /// already hangs off the frame makes the outline grow with it and clamping against it
    /// becomes a no-op. The drawn title block is the paper, so measure that instead.</para>
    ///
    /// <para>The ГОСТ stamp («основная надпись», Форма 3) sits in the bottom-right corner of
    /// the frame and is part of the same family instance, so it cannot be told apart by
    /// bounding box — it is reserved by size instead.</para>
    /// </summary>
    public sealed class SheetFrameGeometry
    {
        public const double MmPerFoot = 304.8;

        /// <summary>Binding margin on the left edge (ГОСТ 2.301).</summary>
        public const double LeftMarginMm = 20;

        /// <summary>Frame inset on the other three edges (ГОСТ 2.301).</summary>
        public const double EdgeMarginMm = 5;

        /// <summary>ГОСТ 21.501 Форма 3 stamp, bottom-right of the frame.</summary>
        public const double StampWidthMm = 185;

        public const double StampHeightMm = 55;

        private const double Tolerance = 1e-9;

        private SheetFrameGeometry(
            double minX,
            double minY,
            double maxX,
            double maxY,
            bool fromTitleBlock)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
            FromTitleBlock = fromTitleBlock;
        }

        /// <summary>Paper rectangle (title block extents when one is present).</summary>
        public double MinX { get; }

        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }

        /// <summary>False when the paper fell back to <see cref="ViewSheet.Outline"/>.</summary>
        public bool FromTitleBlock { get; }

        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;

        /// <summary>Left edge of the printable field (inside the binding margin).</summary>
        public double PrintableMinX => MinX + MmToFeet(LeftMarginMm);

        public double PrintableMinY => MinY + MmToFeet(EdgeMarginMm);
        public double PrintableMaxX => MaxX - MmToFeet(EdgeMarginMm);
        public double PrintableMaxY => MaxY - MmToFeet(EdgeMarginMm);

        public double StampMinX => PrintableMaxX - MmToFeet(StampWidthMm);
        public double StampMaxY => PrintableMinY + MmToFeet(StampHeightMm);

        public static double MmToFeet(double millimeters) => millimeters / MmPerFoot;

        public static double FeetToMm(double feet) => feet * MmPerFoot;

        /// <summary>
        /// Paper frame of <paramref name="sheet"/>: the largest title block instance when the
        /// sheet has one, otherwise the sheet outline.
        /// </summary>
        public static SheetFrameGeometry Resolve(Document doc, ViewSheet sheet)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (sheet == null) throw new ArgumentNullException(nameof(sheet));

            var outline = sheet.Outline;
            var fallback = outline == null
                ? new SheetFrameGeometry(0, 0, MmToFeet(420), MmToFeet(297), false)
                : new SheetFrameGeometry(outline.Min.U, outline.Min.V, outline.Max.U, outline.Max.V, false);

            BoundingBoxXYZ best = null;
            var bestArea = 0.0;

            foreach (var titleBlock in new FilteredElementCollector(doc, sheet.Id)
                         .OfCategory(BuiltInCategory.OST_TitleBlocks)
                         .WhereElementIsNotElementType())
            {
                var bbox = titleBlock.get_BoundingBox(sheet);
                if (bbox == null)
                    continue;

                var area = (bbox.Max.X - bbox.Min.X) * (bbox.Max.Y - bbox.Min.Y);
                if (area <= bestArea)
                    continue;

                bestArea = area;
                best = bbox;
            }

            if (best == null)
                return fallback;

            // Ignore an orphan/degenerate stamp box — anything smaller than A5 is not paper.
            if (FeetToMm(best.Max.X - best.Min.X) < 140 || FeetToMm(best.Max.Y - best.Min.Y) < 100)
                return fallback;

            return new SheetFrameGeometry(best.Min.X, best.Min.Y, best.Max.X, best.Max.Y, true);
        }

        /// <summary>
        /// Lower-left point for a <paramref name="width"/>×<paramref name="height"/> box whose
        /// requested lower-left is <paramref name="requestedX"/>/<paramref name="requestedY"/>
        /// (feet, sheet coordinates): clamped into the printable field and pushed clear of the
        /// stamp when it would cover it. A box larger than the field is pinned to the top-left
        /// corner, so the part that does not fit runs off the bottom where it is obvious,
        /// instead of being centred across the frame and the stamp.
        /// </summary>
        public XYZ FitInside(double width, double height, double requestedX, double requestedY)
        {
            var tooWide = width > PrintableMaxX - PrintableMinX + Tolerance;
            var tooTall = height > PrintableMaxY - PrintableMinY + Tolerance;

            var x = tooWide ? PrintableMinX : Clamp(requestedX, PrintableMinX, PrintableMaxX - width);
            var y = tooTall ? PrintableMaxY - height : Clamp(requestedY, PrintableMinY, PrintableMaxY - height);

            if (tooWide || tooTall || !OverlapsStamp(x, y, width, height))
                return new XYZ(x, y, 0);

            // Above the stamp keeps the reading order of a ГОСТ sheet; left of it is the
            // fallback for a table too tall to sit on the shelf above the stamp.
            var above = StampMaxY;
            if (above + height <= PrintableMaxY + Tolerance)
                return new XYZ(x, above, 0);

            var left = StampMinX - width;
            if (left >= PrintableMinX - Tolerance)
                return new XYZ(left, y, 0);

            return new XYZ(x, y, 0);
        }

        /// <summary>True when the box does not fit the printable field at all.</summary>
        public bool ExceedsPrintable(double width, double height)
        {
            return width > PrintableMaxX - PrintableMinX + Tolerance ||
                   height > PrintableMaxY - PrintableMinY + Tolerance;
        }

        private bool OverlapsStamp(double x, double y, double width, double height)
        {
            return x < PrintableMaxX - Tolerance &&
                   x + width > StampMinX + Tolerance &&
                   y < StampMaxY - Tolerance &&
                   y + height > PrintableMinY + Tolerance;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }
}
