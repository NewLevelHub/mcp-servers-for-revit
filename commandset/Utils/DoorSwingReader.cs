namespace RevitMCPCommandSet.Utils;

/// <summary>
///     The swing of a placed door, measured from its plan representation.
/// </summary>
public sealed class MeasuredDoorSwing
{
    /// <summary>Hinge position (feet, model coords) — the swing arc centre.</summary>
    public XYZ Hinge { get; set; }

    /// <summary>Unit vector hinge → closed leaf tip. Runs along the wall.</summary>
    public XYZ LeafDir { get; set; }

    /// <summary>Unit vector perpendicular to the wall, pointing to the side the door opens toward.</summary>
    public XYZ SwingNormal { get; set; }

    /// <summary>Unit vector opening centre → hinge, i.e. which jamb carries the hinge.</summary>
    public XYZ HingeDir => LeafDir == null ? null : -LeafDir;

    /// <summary>Leaf width in feet — the arc radius.</summary>
    public double WidthFt { get; set; }
}

/// <summary>
///     Reads the swing of a door that is already in the model, instead of inferring it from a
///     family's <c>HandOrientation</c> convention.
///     <para>
///     Revit door families disagree on which way HandOrientation runs, so a dot-product against it
///     mirrors every door of an unlucky family at once — with nothing in the response to show it
///     (REV-152). The plan-view swing arc is the same geometry the DWG underlay draws, so the two
///     compare directly: arc centre = hinge, the endpoint lying along the wall = closed leaf, the
///     other endpoint = open side.
///     </para>
/// </summary>
public static class DoorSwingReader
{
    /// <summary>Accept arcs whose radius is within this factor of the leaf width.</summary>
    private const double RadiusToleranceFactor = 0.35;

    /// <summary>Reject near-flat arcs (fillets, furniture) and full circles.</summary>
    private const double MinSweepRad = 30.0 * Math.PI / 180.0;

    private const double MaxSweepRad = 200.0 * Math.PI / 180.0;

    /// <summary>
    ///     Measures the door's swing in <paramref name="planView" />.
    ///     Returns null when the family draws no usable swing arc — the caller must then say so
    ///     rather than report a verified placement.
    /// </summary>
    /// <param name="door">The placed door instance.</param>
    /// <param name="planView">Plan view whose representation carries the swing symbolics.</param>
    /// <param name="wallDir">Unit direction of the host wall, used to pick the closed endpoint.</param>
    /// <param name="expectedWidthFt">Leaf width; 0 accepts any plausible radius.</param>
    public static MeasuredDoorSwing TryRead(
        FamilyInstance door,
        View planView,
        XYZ wallDir,
        double expectedWidthFt)
    {
        if (door == null || planView == null || wallDir == null)
            return null;

        var arcs = CollectPlanArcs(door, planView);
        if (arcs.Count == 0)
            return null;

        var flatWallDir = Flatten(wallDir);
        if (flatWallDir == null)
            return null;

        Arc best = null;
        var bestScore = double.MaxValue;
        foreach (var arc in arcs)
        {
            var sweep = Math.Abs(arc.GetEndParameter(1) - arc.GetEndParameter(0));
            if (sweep < MinSweepRad || sweep > MaxSweepRad)
                continue;

            // The swing radius IS the leaf width — that alone rejects furniture and fillets.
            if (expectedWidthFt > 0)
            {
                var deviation = Math.Abs(arc.Radius - expectedWidthFt);
                if (deviation > expectedWidthFt * RadiusToleranceFactor)
                    continue;
                if (deviation < bestScore)
                {
                    bestScore = deviation;
                    best = arc;
                }
            }
            else
            {
                // No expectation: the widest sweep is the door, not a detail fillet.
                var score = -sweep;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = arc;
                }
            }
        }

        return best == null ? null : Resolve(best, flatWallDir);
    }

    /// <summary>
    ///     Same endpoint logic as the DWG side (resolveArcSwingOnWall in cadOpeningTracing.ts):
    ///     the endpoint aligned with the wall is the closed leaf, the other one gives the open side.
    /// </summary>
    private static MeasuredDoorSwing Resolve(Arc arc, XYZ wallDir)
    {
        var center = new XYZ(arc.Center.X, arc.Center.Y, 0);
        var ends = new[] { arc.GetEndPoint(0), arc.GetEndPoint(1) };

        XYZ closed = null;
        XYZ open = null;
        var bestAlign = 0.0;
        for (var i = 0; i < 2; i++)
        {
            var v = Flatten(ends[i] - arc.Center);
            if (v == null)
                continue;
            var align = Math.Abs(v.DotProduct(wallDir));
            if (align > bestAlign)
            {
                bestAlign = align;
                closed = v;
                open = Flatten(ends[1 - i] - arc.Center);
            }
        }

        // Neither endpoint lies along the wall: this arc is not a door swing on this wall.
        if (closed == null || open == null || bestAlign < 0.85)
            return null;

        // Strip the along-wall component so the normal is exactly perpendicular.
        var along = open.DotProduct(closed);
        var normal = open - along * closed;
        if (normal.GetLength() < 1e-6)
            return null;

        return new MeasuredDoorSwing
        {
            Hinge = center,
            LeafDir = closed,
            SwingNormal = normal.Normalize(),
            WidthFt = arc.Radius
        };
    }

    private static List<Arc> CollectPlanArcs(FamilyInstance door, View planView)
    {
        var arcs = new List<Arc>();

        // Symbolic swing curves only exist in the view-specific representation.
        var options = new Options
        {
            View = planView,
            ComputeReferences = false,
            IncludeNonVisibleObjects = false
        };

        try
        {
            Harvest(door.get_Geometry(options), arcs, 0);
        }
        catch
        {
            return arcs;
        }

        if (arcs.Count == 0)
        {
            // Some families put the swing on a subcategory that is off in this view.
            try
            {
                options.IncludeNonVisibleObjects = true;
                Harvest(door.get_Geometry(options), arcs, 0);
            }
            catch
            {
                // fall through with whatever was collected
            }
        }

        return arcs;
    }

    private static void Harvest(GeometryElement geometry, List<Arc> arcs, int depth)
    {
        // Nested families go a couple of levels deep; the guard stops pathological recursion.
        if (geometry == null || depth > 4)
            return;

        foreach (var obj in geometry)
            switch (obj)
            {
                case Arc arc when IsHorizontal(arc):
                    arcs.Add(arc);
                    break;
                case GeometryInstance instance:
                    Harvest(instance.GetInstanceGeometry(), arcs, depth + 1);
                    break;
                case GeometryElement nested:
                    Harvest(nested, arcs, depth + 1);
                    break;
            }
    }

    /// <summary>A door swing lies in plan; a curved mullion or handle profile does not.</summary>
    private static bool IsHorizontal(Arc arc)
    {
        try
        {
            return Math.Abs(Math.Abs(arc.Normal.Z) - 1.0) < 1e-3;
        }
        catch
        {
            return false;
        }
    }

    private static XYZ Flatten(XYZ v)
    {
        if (v == null)
            return null;
        var flat = new XYZ(v.X, v.Y, 0);
        return flat.GetLength() < 1e-9 ? null : flat.Normalize();
    }
}
