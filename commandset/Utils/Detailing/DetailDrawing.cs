using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils.Detailing;

/// <summary>
///     Low-level drawing on a detail/drafting/plan view: curves, filled regions, line styles.
///     <para>
///     Nothing here opens a transaction. Revit forbids nesting them, and the node generator needs
///     one transaction around a whole detail, so transaction ownership stays with the caller.
///     </para>
/// </summary>
public static class DetailDrawing
{
    /// <summary>Segments shorter than this are duplicate points, not geometry.</summary>
    public const double MinSegmentLengthMm = 1.0;

    /// <summary>Views that accept detail curves and filled regions.</summary>
    public static bool SupportsDetailing(View view)
    {
        return view is ViewPlan || view is ViewDrafting ||
               view.ViewType == ViewType.Detail || view.ViewType == ViewType.DraftingView ||
               view.ViewType == ViewType.Section;
    }

    /// <summary>
    ///     Elevation of the view plane in internal units. Detail curves on a plan must sit at the
    ///     level elevation; a drafting view has no level and works at zero.
    /// </summary>
    public static double ViewPlaneZ(View view)
    {
        return view is ViewPlan plan && plan.GenLevel != null ? plan.GenLevel.Elevation : 0;
    }

    public static XYZ ToViewPoint(double xMm, double yMm, double z)
    {
        return new XYZ(
            RevitUnitConversion.FromMillimeters(xMm),
            RevitUnitConversion.FromMillimeters(yMm),
            z);
    }

    /// <summary>Draws one segment, or returns null when the two points coincide.</summary>
    public static DetailCurve DrawSegment(Document doc, View view, XYZ start, XYZ end)
    {
        if (start.DistanceTo(end) < RevitUnitConversion.FromMillimeters(MinSegmentLengthMm))
            return null;

        return doc.Create.NewDetailCurve(view, Line.CreateBound(start, end));
    }

    /// <summary>
    ///     Draws consecutive points as detail curves. <paramref name="close" /> adds the closing
    ///     segment back to the first point.
    /// </summary>
    public static List<DetailCurve> DrawPolyline(
        Document doc,
        View view,
        IReadOnlyList<XYZ> points,
        bool close = false)
    {
        var created = new List<DetailCurve>();
        if (points == null || points.Count < 2)
            return created;

        var count = close ? points.Count : points.Count - 1;
        for (var i = 0; i < count; i++)
        {
            var start = points[i];
            var end = points[(i + 1) % points.Count];

            var curve = DrawSegment(doc, view, start, end);
            if (curve != null)
                created.Add(curve);
        }

        return created;
    }

    /// <summary>Arc through three points: two endpoints and a point on the arc between them.</summary>
    public static DetailCurve DrawArc(Document doc, View view, XYZ start, XYZ end, XYZ pointOnArc)
    {
        var arc = Arc.Create(start, end, pointOnArc);
        return doc.Create.NewDetailCurve(view, arc);
    }

    /// <summary>
    ///     Drops duplicate consecutive points (and a closing point that repeats the first) from a
    ///     contour, within <see cref="MinSegmentLengthMm" />. Shared by every "build a closed shape
    ///     from points" helper below — a zero-length edge is what both CurveLoop.Append and
    ///     RevisionCloud.Create refuse.
    /// </summary>
    private static List<XYZ> CleanClosedContour(IReadOnlyList<XYZ> points)
    {
        if (points == null || points.Count < 3)
            throw new ArgumentException("A closed contour needs at least 3 points.");

        var cleaned = new List<XYZ>();
        var tolerance = RevitUnitConversion.FromMillimeters(MinSegmentLengthMm);

        foreach (var point in points)
        {
            if (cleaned.Count == 0 || cleaned[cleaned.Count - 1].DistanceTo(point) >= tolerance)
                cleaned.Add(point);
        }

        while (cleaned.Count > 1 && cleaned[0].DistanceTo(cleaned[cleaned.Count - 1]) < tolerance)
            cleaned.RemoveAt(cleaned.Count - 1);

        if (cleaned.Count < 3)
            throw new ArgumentException("A closed contour needs at least 3 distinct points.");

        return cleaned;
    }

    /// <summary>
    ///     Builds a closed loop from mm coordinates. Duplicate consecutive points are dropped —
    ///     CurveLoop.Append throws on zero-length curves.
    /// </summary>
    public static CurveLoop BuildClosedLoop(IReadOnlyList<XYZ> points)
    {
        var cleaned = CleanClosedContour(points);

        var loop = new CurveLoop();
        for (var i = 0; i < cleaned.Count; i++)
            loop.Append(Line.CreateBound(cleaned[i], cleaned[(i + 1) % cleaned.Count]));

        return loop;
    }

    /// <summary>
    ///     Same shape as <see cref="BuildClosedLoop" />, as a flat curve list — what
    ///     <c>RevisionCloud.Create</c> takes instead of a <c>CurveLoop</c>.
    /// </summary>
    public static List<Curve> BuildClosedCurves(IReadOnlyList<XYZ> points)
    {
        var cleaned = CleanClosedContour(points);

        var curves = new List<Curve>();
        for (var i = 0; i < cleaned.Count; i++)
            curves.Add(Line.CreateBound(cleaned[i], cleaned[(i + 1) % cleaned.Count]));

        return curves;
    }

    public static FilledRegion FillContour(
        Document doc,
        View view,
        IList<CurveLoop> loops,
        ElementId filledRegionTypeId)
    {
        return FilledRegion.Create(doc, filledRegionTypeId, view.Id, loops);
    }

    /// <summary>
    ///     Line styles in Revit are subcategories of OST_Lines, not standalone elements. Returns the
    ///     projection graphics style for the named subcategory, or null when it does not exist.
    /// </summary>
    public static GraphicsStyle ResolveLineStyle(Document doc, string styleName)
    {
        if (doc == null || string.IsNullOrWhiteSpace(styleName))
            return null;

        var wanted = styleName.Trim();
        var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        if (lines == null)
            return null;

        if (lines.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            return lines.GetGraphicsStyle(GraphicsStyleType.Projection);

        foreach (Category subCategory in lines.SubCategories)
        {
            if (subCategory.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                return subCategory.GetGraphicsStyle(GraphicsStyleType.Projection);
        }

        return null;
    }

    public static List<string> CollectLineStyleNames(Document doc)
    {
        var names = new List<string>();
        var lines = doc?.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        if (lines == null)
            return names;

        foreach (Category subCategory in lines.SubCategories)
        {
            if (!string.IsNullOrWhiteSpace(subCategory.Name))
                names.Add(subCategory.Name);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    ///     Applies a line style to a curve. Some styles are locked by the template and refuse
    ///     assignment, so failure is reported instead of thrown.
    /// </summary>
    public static bool TryApplyLineStyle(CurveElement curve, GraphicsStyle style, out string error)
    {
        error = null;
        if (curve == null || style == null)
            return false;

        try
        {
            curve.LineStyle = style;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
