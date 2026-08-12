namespace RevitMCPCommandSet.Utils.Detailing;

/// <summary>
///     Turns a list of build-up layers into the contours, lines, dimensions and notes of a node,
///     in mm, with no Revit API involved.
///     <para>
///     Kept free of Revit types on purpose: the layout rules (where the gap goes, how far the
///     dimension chain sits, how notes avoid each other) are the part worth testing, and a test
///     that needs a running Revit never gets run.
///     </para>
/// </summary>
public static class NodeDetailGeometryBuilder
{
    /// <summary>Distance from the drawing to the dimension chain, in paper mm.</summary>
    private const double DimensionOffsetPaperMm = 10;

    /// <summary>Distance from the drawing to the column of notes, in paper mm.</summary>
    private const double NoteOffsetPaperMm = 26;

    /// <summary>Minimum spacing between note rows, in paper mm.</summary>
    private const double NoteRowPaperMm = 6;

    /// <summary>How far the wall is drawn below the underside of the floor, mm.</summary>
    private const double WallBelowFloorMm = 150;

    public static NodeDetailGeometry Build(NodeDetailRequest request)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        var mode = (request.Mode ?? "junction").Trim().ToLowerInvariant();

        var geometry = mode switch
        {
            "single" => BuildSingle(request),
            "junction" => BuildJunction(request),
            _ => throw new ArgumentException($"Unknown mode '{request.Mode}'. Use 'single' or 'junction'.")
        };

        geometry.Bounds = MeasureBounds(geometry);
        return geometry;
    }

    // ---------------------------------------------------------------- single

    private static NodeDetailGeometry BuildSingle(NodeDetailRequest request)
    {
        var layers = Sanitize(request.PrimaryLayers, "assembly");
        var geometry = new NodeDetailGeometry();
        var paper = PaperFactor(request.Scale);
        var length = request.LengthMm > 0 ? request.LengthMm : 600;

        var vertical = (request.Orientation ?? "horizontal").Trim()
            .Equals("vertical", StringComparison.OrdinalIgnoreCase);

        if (vertical)
        {
            // Layers stack left to right, the assembly runs upwards: a wall in section.
            var placement = StackLayers(
                geometry,
                layers,
                Axis.X,
                stackFrom: 0,
                direction: 1,
                crossFrom: 0,
                crossTo: length,
                inset: _ => 0);

            if (!request.Annotate)
                return geometry;

            AddChain(geometry, placement, Axis.X, linePosition: length + DimensionOffsetPaperMm * paper);
            AddNotes(geometry, placement, Axis.X, columnX: -NoteOffsetPaperMm * paper, paper, alignEnd: false);
            return geometry;
        }

        // Layers stack downwards from the finished surface, the assembly runs to the right: a floor.
        var floor = StackLayers(
            geometry,
            layers,
            Axis.Y,
            stackFrom: 0,
            direction: -1,
            crossFrom: 0,
            crossTo: length,
            inset: _ => 0);

        if (!request.Annotate)
            return geometry;

        AddChain(geometry, floor, Axis.Y, linePosition: length + DimensionOffsetPaperMm * paper);
        AddNotes(geometry, floor, Axis.Y, columnX: length + NoteOffsetPaperMm * paper, paper, alignEnd: true);
        return geometry;
    }

    // -------------------------------------------------------------- junction

    private static NodeDetailGeometry BuildJunction(NodeDetailRequest request)
    {
        var wallLayers = Sanitize(request.PrimaryLayers, "wall");
        var floorLayers = Sanitize(request.SecondaryLayers, "floor");

        if (floorLayers.Count == 0)
        {
            throw new ArgumentException(
                "Junction mode needs both assemblies: pass a floor as well as a wall, " +
                "or use mode 'single' for one build-up.");
        }

        var geometry = new NodeDetailGeometry();
        var paper = PaperFactor(request.Scale);
        var length = request.LengthMm > 0 ? request.LengthMm : 600;
        var wallRun = request.WallRunMm > 0 ? request.WallRunMm : 500;
        var gap = Math.Max(0, request.GapMm);

        var wallThickness = wallLayers.Sum(layer => layer.ThicknessMm);
        var floorThickness = floorLayers.Sum(layer => layer.ThicknessMm);

        // Finished floor level is y = 0; the wall face the floor meets is x = wallThickness.
        var wallBottom = -floorThickness - WallBelowFloorMm;

        var wall = StackLayers(
            geometry,
            wallLayers,
            Axis.X,
            stackFrom: 0,
            direction: 1,
            crossFrom: wallBottom,
            crossTo: wallRun,
            inset: _ => 0);

        // Screed and finish stop short of the wall; the structural slab runs into it. That gap is
        // the whole point of this node, so it is geometry, not decoration.
        var firstCore = floorLayers.FindIndex(layer => layer.IsCore);
        if (gap > 0 && firstCore <= 0)
        {
            geometry.Warnings.Add(
                "The floor type declares no finish layers above its core, so the expansion gap " +
                "at the wall was not drawn. Check the layer functions of the floor type.");
        }

        var floor = StackLayers(
            geometry,
            floorLayers,
            Axis.Y,
            stackFrom: 0,
            direction: -1,
            crossFrom: wallThickness,
            crossTo: wallThickness + length,
            inset: index => firstCore > 0 && index < firstCore ? gap : 0);

        // The gap face itself is drawn by StackLayers as the inset run's own edge, tagged "gap".

        if (!request.Annotate)
            return geometry;

        AddChain(geometry, wall, Axis.X, linePosition: wallRun + DimensionOffsetPaperMm * paper);
        AddChain(geometry, floor, Axis.Y, linePosition: wallThickness + length + DimensionOffsetPaperMm * paper);

        AddNotes(geometry, wall, Axis.X, columnX: -NoteOffsetPaperMm * paper, paper, alignEnd: false);
        AddNotes(
            geometry,
            floor,
            Axis.Y,
            columnX: wallThickness + length + NoteOffsetPaperMm * paper,
            paper,
            alignEnd: true);

        return geometry;
    }

    // --------------------------------------------------------------- layout

    private enum Axis
    {
        X,
        Y
    }

    /// <summary>The one division line drawn at a boundary coordinate, and how far it reaches.</summary>
    private class BoundaryLine
    {
        public int SegmentIndex { get; set; }

        public double CrossFrom { get; set; }
    }

    /// <summary>Where each layer ended up, so annotations can be hung off it afterwards.</summary>
    private class StackPlacement
    {
        public List<NodeLayerSpec> Layers { get; } = new List<NodeLayerSpec>();

        /// <summary>Coordinate of each layer boundary along the stacking axis, layers + 1 entries.</summary>
        public List<double> Boundaries { get; } = new List<double>();

        /// <summary>Index in NodeDetailGeometry.Segments of the division line at each boundary.</summary>
        public List<int> DivisionSegments { get; } = new List<int>();

        /// <summary>Extent of each layer across the stacking axis, after any inset.</summary>
        public List<double> CrossFrom { get; } = new List<double>();

        public List<double> CrossTo { get; } = new List<double>();

        public double OuterCrossFrom { get; set; }

        public double OuterCrossTo { get; set; }
    }

    private static StackPlacement StackLayers(
        NodeDetailGeometry geometry,
        IReadOnlyList<NodeLayerSpec> layers,
        Axis axis,
        double stackFrom,
        int direction,
        double crossFrom,
        double crossTo,
        Func<int, double> inset)
    {
        var placement = new StackPlacement
        {
            OuterCrossFrom = crossFrom,
            OuterCrossTo = crossTo
        };

        var cursor = stackFrom;
        placement.Boundaries.Add(cursor);

        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            var start = cursor;
            var end = cursor + direction * layer.ThicknessMm;

            var layerCrossFrom = crossFrom + inset(i);

            placement.Layers.Add(layer);
            placement.CrossFrom.Add(layerCrossFrom);
            placement.CrossTo.Add(crossTo);
            placement.Boundaries.Add(end);

            if (!layer.IsZeroWidth)
            {
                geometry.Contours.Add(new NodeContour
                {
                    Label = DescribeLayer(layer),
                    HatchPattern = layer.HatchPattern,
                    Points = Rectangle(axis, start, end, layerCrossFrom, crossTo)
                });
            }

            cursor = end;
        }

        // Division lines run the full width of the widest layer touching them, so a chain of
        // dimensions can pick every boundary even where the finish is held back from the wall.
        var lineAtBoundary = new Dictionary<double, BoundaryLine>();

        for (var i = 0; i < placement.Boundaries.Count; i++)
        {
            var before = i > 0 ? placement.CrossFrom[i - 1] : double.MaxValue;
            var after = i < layers.Count ? placement.CrossFrom[i] : double.MaxValue;
            var lineCrossFrom = Math.Min(before, after);
            if (lineCrossFrom == double.MaxValue)
                lineCrossFrom = crossFrom;

            var coordinate = Math.Round(placement.Boundaries[i], 3);

            // A zero-width layer puts two boundaries at the same coordinate. They share one
            // line rather than stacking two identical curves in the view, and that line spans
            // whichever of the two neighbouring layers reaches further.
            if (lineAtBoundary.TryGetValue(coordinate, out var existing))
            {
                if (lineCrossFrom < existing.CrossFrom)
                {
                    geometry.Segments[existing.SegmentIndex].Start =
                        PointOn(axis, placement.Boundaries[i], lineCrossFrom);
                    existing.CrossFrom = lineCrossFrom;
                }

                placement.DivisionSegments.Add(existing.SegmentIndex);
                continue;
            }

            lineAtBoundary[coordinate] = new BoundaryLine
            {
                SegmentIndex = geometry.Segments.Count,
                CrossFrom = lineCrossFrom
            };

            placement.DivisionSegments.Add(geometry.Segments.Count);
            geometry.Segments.Add(new NodeSegment
            {
                Start = PointOn(axis, placement.Boundaries[i], lineCrossFrom),
                End = PointOn(axis, placement.Boundaries[i], crossTo),
                Role = "division"
            });
        }

        // The two long edges of the assembly.
        var stackStart = placement.Boundaries[0];
        var stackEnd = placement.Boundaries[placement.Boundaries.Count - 1];

        geometry.Segments.Add(new NodeSegment
        {
            Start = PointOn(axis, stackStart, crossTo),
            End = PointOn(axis, stackEnd, crossTo),
            Role = "outline"
        });

        // One edge per run of layers sharing a face. Layers held back by the same inset are a
        // single line rather than one piece each — drawing them piecewise and then adding the
        // gap line on top put two curves along the same face. A zero-width layer inside a run
        // adds no length, and a run made only of them is skipped: Revit rejects a zero-length line.
        var runStart = 0;
        while (runStart < layers.Count)
        {
            var runEnd = runStart;
            while (runEnd + 1 < layers.Count &&
                   Math.Abs(placement.CrossFrom[runEnd + 1] - placement.CrossFrom[runStart]) < 0.001)
                runEnd++;

            var face = placement.CrossFrom[runStart];
            var from = placement.Boundaries[runStart];
            var to = placement.Boundaries[runEnd + 1];

            if (Math.Abs(to - from) > 0.001)
            {
                geometry.Segments.Add(new NodeSegment
                {
                    Start = PointOn(axis, from, face),
                    End = PointOn(axis, to, face),
                    // An inset face is the expansion gap: worth its own role so it can be styled.
                    Role = face > crossFrom + 0.001 ? "gap" : "outline"
                });
            }

            runStart = runEnd + 1;
        }

        return placement;
    }

    private static void AddChain(
        NodeDetailGeometry geometry,
        StackPlacement placement,
        Axis axis,
        double linePosition)
    {
        if (placement.Boundaries.Count < 2)
            return;

        var indices = new List<int>();
        var seen = new HashSet<double>();

        for (var i = 0; i < placement.Boundaries.Count; i++)
        {
            // Zero-width layers put two boundaries at the same coordinate; a dimension between
            // them would read 0 and Revit refuses to place it.
            if (!seen.Add(Math.Round(placement.Boundaries[i], 3)))
                continue;

            indices.Add(placement.DivisionSegments[i]);
        }

        if (indices.Count < 2)
            return;

        var first = placement.Boundaries[0];
        var last = placement.Boundaries[placement.Boundaries.Count - 1];

        geometry.Dimensions.Add(new NodeDimensionSpec
        {
            SegmentIndices = indices,
            LineStart = PointOn(axis, first, linePosition),
            LineEnd = PointOn(axis, last, linePosition),
            Label = axis == Axis.X ? "thickness chain" : "layer chain"
        });
    }

    /// <summary>
    ///     Labels always form a column: one row per layer, rows pushed apart vertically, every
    ///     label hanging off the same edge.
    ///     <para>
    ///     Spreading labels along the stacking axis only reads well when that axis runs down the
    ///     sheet. A wall stacks sideways, so dealing its labels out along that axis put them along
    ///     the very direction the text grows — 70 mm of spacing against an 800 mm string, and every
    ///     label landed on the one before it.
    ///     </para>
    /// </summary>
    private static void AddNotes(
        NodeDetailGeometry geometry,
        StackPlacement placement,
        Axis axis,
        double columnX,
        double paper,
        bool alignEnd)
    {
        var count = placement.Layers.Count;
        if (count == 0)
            return;

        var rowStep = NoteRowPaperMm * paper;

        var midpoints = new List<double>();
        for (var i = 0; i < count; i++)
            midpoints.Add((placement.Boundaries[i] + placement.Boundaries[i + 1]) / 2);

        List<double> rows;
        if (axis == Axis.Y)
        {
            // The stack already runs down the sheet, so each label can sit beside its own layer.
            rows = SpreadRows(midpoints, rowStep);
        }
        else
        {
            // The stack runs across the sheet: the labels get a column of their own, starting at
            // the top of the assembly and stepping down.
            rows = new List<double>();
            for (var i = 0; i < count; i++)
                rows.Add(placement.OuterCrossTo - (i + 1) * rowStep);
        }

        for (var i = 0; i < count; i++)
        {
            var leaderEnd = axis == Axis.Y
                ? PointOn(axis, midpoints[i], alignEnd ? placement.CrossTo[i] : placement.CrossFrom[i])
                // A horizontal leader reaching its layer at the height of its own row.
                : PointOn(axis, midpoints[i], rows[i]);

            geometry.Notes.Add(new NodeNoteSpec
            {
                Text = DescribeLayer(placement.Layers[i]),
                Position = new NodePoint(columnX, rows[i]),
                LeaderEnd = leaderEnd,
                // A column left of the drawing has to grow leftwards, or the text runs over the node.
                AlignRight = leaderEnd.X > columnX
            });
        }
    }

    /// <summary>
    ///     Pushes note rows apart so thin layers do not stack their labels on top of each other,
    ///     while keeping each label as close to its layer as the spacing allows.
    /// </summary>
    public static List<double> SpreadRows(IReadOnlyList<double> preferred, double minSpacing)
    {
        var rows = preferred.ToList();
        if (rows.Count < 2 || minSpacing <= 0)
            return rows;

        var descending = rows[rows.Count - 1] < rows[0];

        for (var i = 1; i < rows.Count; i++)
        {
            if (descending)
            {
                if (rows[i - 1] - rows[i] < minSpacing)
                    rows[i] = rows[i - 1] - minSpacing;
            }
            else
            {
                if (rows[i] - rows[i - 1] < minSpacing)
                    rows[i] = rows[i - 1] + minSpacing;
            }
        }

        return rows;
    }

    // --------------------------------------------------------------- helpers

    private static List<NodeLayerSpec> Sanitize(IEnumerable<NodeLayerSpec> layers, string what)
    {
        var cleaned = (layers ?? Enumerable.Empty<NodeLayerSpec>())
            .Where(layer => layer != null)
            .ToList();

        if (cleaned.Count == 0)
            throw new ArgumentException($"No {what} layers to draw.");

        foreach (var layer in cleaned)
        {
            if (layer.ThicknessMm < 0)
                throw new ArgumentException($"Layer '{layer.Name}' has a negative thickness.");

            if (layer.ThicknessMm <= 0.01)
                layer.IsZeroWidth = true;
        }

        return cleaned;
    }

    private static double SumTo(IReadOnlyList<NodeLayerSpec> layers, int count)
    {
        double total = 0;
        for (var i = 0; i < count && i < layers.Count; i++)
            total += layers[i].ThicknessMm;

        return total;
    }

    private static string DescribeLayer(NodeLayerSpec layer)
    {
        var name = string.IsNullOrWhiteSpace(layer.Name) ? layer.Function : layer.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = "Слой";

        return layer.IsZeroWidth
            ? name
            : $"{name}, {FormatThickness(layer.ThicknessMm)} мм";
    }

    private static string FormatThickness(double thicknessMm)
    {
        var rounded = Math.Round(thicknessMm, 1);
        return Math.Abs(rounded - Math.Round(rounded)) < 0.05
            ? Math.Round(rounded).ToString("0")
            : rounded.ToString("0.#");
    }

    /// <summary>
    ///     Model mm per paper mm. Text and dimensions are sized on paper, so every annotation
    ///     offset has to be multiplied by the scale or a 1:5 node overlaps and a 1:20 one scatters.
    /// </summary>
    private static double PaperFactor(int scale) => scale > 0 ? scale : 10;

    private static NodePoint PointOn(Axis axis, double alongStack, double acrossStack)
    {
        return axis == Axis.X
            ? new NodePoint(alongStack, acrossStack)
            : new NodePoint(acrossStack, alongStack);
    }

    private static List<NodePoint> Rectangle(Axis axis, double from, double to, double crossFrom, double crossTo)
    {
        return new List<NodePoint>
        {
            PointOn(axis, from, crossFrom),
            PointOn(axis, to, crossFrom),
            PointOn(axis, to, crossTo),
            PointOn(axis, from, crossTo)
        };
    }

    private static NodeBounds MeasureBounds(NodeDetailGeometry geometry)
    {
        var points = geometry.Contours.SelectMany(contour => contour.Points)
            .Concat(geometry.Segments.SelectMany(segment => new[] { segment.Start, segment.End }))
            .Concat(geometry.Notes.SelectMany(note => new[] { note.Position, note.LeaderEnd }))
            .Where(point => point != null)
            .ToList();

        if (points.Count == 0)
            return new NodeBounds();

        return new NodeBounds
        {
            MinX = points.Min(point => point.X),
            MinY = points.Min(point => point.Y),
            MaxX = points.Max(point => point.X),
            MaxY = points.Max(point => point.Y)
        };
    }
}
