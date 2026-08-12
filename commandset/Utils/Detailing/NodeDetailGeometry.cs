namespace RevitMCPCommandSet.Utils.Detailing;

/// <summary>A point in the drawing plane of the detail, mm.</summary>
public class NodePoint
{
    public NodePoint()
    {
    }

    public NodePoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    public double X { get; set; }

    public double Y { get; set; }
}

/// <summary>One layer of an assembly as the detail needs to draw it.</summary>
public class NodeLayerSpec
{
    /// <summary>Material name when known, otherwise the layer function.</summary>
    public string Name { get; set; } = string.Empty;

    public double ThicknessMm { get; set; }

    public string Function { get; set; } = string.Empty;

    /// <summary>Fill pattern name to hatch this layer with; empty leaves the layer unhatched.</summary>
    public string HatchPattern { get; set; } = string.Empty;

    /// <summary>Membranes carry no thickness and are drawn as a single line.</summary>
    public bool IsZeroWidth { get; set; }

    /// <summary>Part of the structural core. Finishes above the core are the ones held off the wall.</summary>
    public bool IsCore { get; set; }
}

/// <summary>A hatched area of the detail.</summary>
public class NodeContour
{
    public string Label { get; set; } = string.Empty;

    public string HatchPattern { get; set; } = string.Empty;

    public List<NodePoint> Points { get; set; } = new List<NodePoint>();
}

/// <summary>
///     A straight line of the detail. Division lines separate layers and double as the references
///     a dimension chain attaches to; outlines close the drawing.
/// </summary>
public class NodeSegment
{
    public NodePoint Start { get; set; }

    public NodePoint End { get; set; }

    /// <summary>division, outline or gap.</summary>
    public string Role { get; set; } = "outline";
}

/// <summary>
///     A dimension chain described by the segments it measures between, not by coordinates —
///     the handler holds the created detail curves and can attach exact references.
/// </summary>
public class NodeDimensionSpec
{
    public List<int> SegmentIndices { get; set; } = new List<int>();

    public NodePoint LineStart { get; set; }

    public NodePoint LineEnd { get; set; }

    public string Label { get; set; } = string.Empty;
}

public class NodeNoteSpec
{
    public string Text { get; set; } = string.Empty;

    public NodePoint Position { get; set; }

    public NodePoint LeaderEnd { get; set; }

    /// <summary>
    ///     The label hangs to the left of its leader, so its text has to grow leftwards from the
    ///     anchor. Left-aligned text there would run straight across the node.
    /// </summary>
    public bool AlignRight { get; set; }
}

public class NodeBounds
{
    public double MinX { get; set; }

    public double MinY { get; set; }

    public double MaxX { get; set; }

    public double MaxY { get; set; }
}

public class NodeDetailGeometry
{
    public List<NodeContour> Contours { get; set; } = new List<NodeContour>();

    public List<NodeSegment> Segments { get; set; } = new List<NodeSegment>();

    public List<NodeDimensionSpec> Dimensions { get; set; } = new List<NodeDimensionSpec>();

    public List<NodeNoteSpec> Notes { get; set; } = new List<NodeNoteSpec>();

    public NodeBounds Bounds { get; set; } = new NodeBounds();

    public List<string> Warnings { get; set; } = new List<string>();
}

public class NodeDetailRequest
{
    /// <summary>single — one assembly in section; junction — a floor meeting a wall at 90°.</summary>
    public string Mode { get; set; } = "junction";

    /// <summary>Wall layers in junction mode; the only assembly in single mode.</summary>
    public List<NodeLayerSpec> PrimaryLayers { get; set; } = new List<NodeLayerSpec>();

    /// <summary>Floor layers, junction mode only.</summary>
    public List<NodeLayerSpec> SecondaryLayers { get; set; } = new List<NodeLayerSpec>();

    /// <summary>single mode: vertical stacks layers left to right (a wall), horizontal top down (a floor).</summary>
    public string Orientation { get; set; } = "horizontal";

    /// <summary>Visible run of the assembly along the drawing, mm.</summary>
    public double LengthMm { get; set; } = 600;

    /// <summary>How far the wall is drawn above the floor level, mm.</summary>
    public double WallRunMm { get; set; } = 500;

    /// <summary>Expansion gap between the screed/finish build-up and the wall, mm.</summary>
    public double GapMm { get; set; } = 20;

    /// <summary>View scale denominator; annotation offsets are paper mm multiplied by it.</summary>
    public int Scale { get; set; } = 10;

    public bool Annotate { get; set; } = true;
}
