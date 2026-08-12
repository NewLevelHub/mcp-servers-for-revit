namespace RevitMCPCommandSet.Utils.Detailing;

/// <summary>
///     The orientation and extents of a section, in plain numbers. Converted to a
///     <c>BoundingBoxXYZ</c> by the caller.
/// </summary>
public class SectionFrame
{
    public double[] Origin { get; set; } = new double[3];

    /// <summary>Right of the view, along the section line.</summary>
    public double[] BasisX { get; set; } = new double[3];

    /// <summary>Up of the view, world Z for a vertical cut.</summary>
    public double[] BasisY { get; set; } = new double[3];

    /// <summary>The viewer looks along the negative of this.</summary>
    public double[] BasisZ { get; set; } = new double[3];

    public double MinU { get; set; }

    public double MaxU { get; set; }

    public double MinV { get; set; }

    public double MaxV { get; set; }

    /// <summary>Far clip, negative: everything the viewer sees lies at negative W.</summary>
    public double MinW { get; set; }

    public double MaxW { get; set; }

    /// <summary>Where the section looks — the negated BasisZ, handy for reporting back.</summary>
    public double[] LookDirection => new[] { -BasisZ[0], -BasisZ[1], -BasisZ[2] };
}

/// <summary>
///     Builds the frame of a vertical section from a section line.
///     <para>
///     Deliberately free of Revit types so the handedness — which side of the line the section
///     looks at — can be tested rather than trusted. Drawing the line left to right makes the
///     section look "up the page" in plan; <c>flip</c> turns it around, and the resulting look
///     direction is reported so a wrong guess is visible instead of silent.
///     </para>
///     All lengths are in Revit internal units (feet).
/// </summary>
public static class SectionBoxBuilder
{
    private const double MinLineLength = 1e-6;

    public static SectionFrame FromLine(
        double[] start,
        double[] end,
        double bottom,
        double top,
        double depth,
        bool flip = false)
    {
        if (start == null || start.Length < 2)
            throw new ArgumentException("Section line start needs at least X and Y.", nameof(start));
        if (end == null || end.Length < 2)
            throw new ArgumentException("Section line end needs at least X and Y.", nameof(end));

        var dx = end[0] - start[0];
        var dy = end[1] - start[1];
        var length = Math.Sqrt(dx * dx + dy * dy);

        if (length < MinLineLength)
            throw new ArgumentException("Section line start and end coincide — no direction to cut along.");

        if (top <= bottom)
            throw new ArgumentException("Section top must be above its bottom.");

        if (depth <= 0)
            throw new ArgumentException("Section depth must be positive.");

        var basisX = new[] { dx / length, dy / length, 0.0 };
        var basisY = new[] { 0.0, 0.0, 1.0 };
        var basisZ = Cross(basisX, basisY);

        if (flip)
        {
            basisX = Negate(basisX);
            basisZ = Negate(basisZ);
        }

        return new SectionFrame
        {
            Origin = new[]
            {
                (start[0] + end[0]) / 2,
                (start[1] + end[1]) / 2,
                (bottom + top) / 2
            },
            BasisX = basisX,
            BasisY = basisY,
            BasisZ = basisZ,
            MinU = -length / 2,
            MaxU = length / 2,
            MinV = -(top - bottom) / 2,
            MaxV = (top - bottom) / 2,
            MinW = -depth,
            MaxW = 0
        };
    }

    /// <summary>
    ///     A section across a bounding box. <paramref name="alongX" /> cuts with the section line
    ///     running along X (looking down the Y axis), otherwise along Y.
    /// </summary>
    public static SectionFrame FromBoundingBox(
        double[] min,
        double[] max,
        bool alongX,
        double padding,
        double depth,
        bool flip = false)
    {
        if (min == null || min.Length < 3 || max == null || max.Length < 3)
            throw new ArgumentException("Bounding box needs three-component min and max.");

        var midX = (min[0] + max[0]) / 2;
        var midY = (min[1] + max[1]) / 2;

        var start = alongX
            ? new[] { min[0] - padding, midY, 0.0 }
            : new[] { midX, min[1] - padding, 0.0 };

        var end = alongX
            ? new[] { max[0] + padding, midY, 0.0 }
            : new[] { midX, max[1] + padding, 0.0 };

        var effectiveDepth = depth > 0
            ? depth
            : (alongX ? (max[1] - min[1]) / 2 + padding : (max[0] - min[0]) / 2 + padding);

        return FromLine(
            start,
            end,
            min[2] - padding,
            max[2] + padding,
            Math.Max(effectiveDepth, padding + MinLineLength),
            flip);
    }

    private static double[] Cross(double[] a, double[] b)
    {
        return new[]
        {
            a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0]
        };
    }

    private static double[] Negate(double[] v) => new[] { -v[0], -v[1], -v[2] };
}
