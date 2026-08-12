using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.Detailing;

/// <summary>
///     Information for detail view creation (callout of a model view or a drafting view)
/// </summary>
public class DetailViewCreationInfo
{
    /// <summary>
    ///     callout — detail callout of a parent model view; drafting — independent drafting view;
    ///     section — a real cut through the model, which draws compound layers itself
    /// </summary>
    [JsonProperty("mode")]
    public string Mode { get; set; } = "callout";

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     View scale denominator, e.g. 10 for 1:10
    /// </summary>
    [JsonProperty("scale")]
    public int Scale { get; set; } = 10;

    /// <summary>
    ///     Detail level of the created view: Coarse, Medium, or Fine
    /// </summary>
    [JsonProperty("detailLevel")]
    public string DetailLevel { get; set; } = "Fine";

    /// <summary>
    ///     When true (default), switch the UI to the created view after creation.
    /// </summary>
    [JsonProperty("activateView")]
    public bool ActivateView { get; set; } = true;

    [JsonProperty("parentViewId")]
    public long ParentViewId { get; set; }

    [JsonProperty("parentViewUniqueId")]
    public string ParentViewUniqueId { get; set; } = string.Empty;

    [JsonProperty("parentViewName")]
    public string ParentViewName { get; set; } = string.Empty;

    /// <summary>
    ///     Element whose bounding box (plus padding) defines the callout area
    /// </summary>
    [JsonProperty("elementId")]
    public long ElementId { get; set; }

    /// <summary>
    ///     Padding around the element bounding box, mm
    /// </summary>
    [JsonProperty("padding")]
    public double Padding { get; set; } = 300;

    /// <summary>
    ///     Explicit callout area corner (model coordinates, mm) when elementId is not used
    /// </summary>
    [JsonProperty("areaMin")]
    public JZPoint AreaMin { get; set; }

    [JsonProperty("areaMax")]
    public JZPoint AreaMax { get; set; }

    /// <summary>section mode: start of the cutting line, mm. Used with sectionEnd.</summary>
    [JsonProperty("sectionStart")]
    public JZPoint SectionStart { get; set; }

    [JsonProperty("sectionEnd")]
    public JZPoint SectionEnd { get; set; }

    /// <summary>section mode: bottom of the cut, mm. Defaults to the element bbox or 0.</summary>
    [JsonProperty("sectionBottomMm")]
    public double SectionBottomMm { get; set; }

    [JsonProperty("sectionTopMm")]
    public double SectionTopMm { get; set; }

    /// <summary>section mode: how far in front of the cut plane stays visible, mm.</summary>
    [JsonProperty("sectionDepthMm")]
    public double SectionDepthMm { get; set; } = 2000;

    /// <summary>section mode with elementId: cut along X (default) or along Y.</summary>
    [JsonProperty("sectionAlongX")]
    public bool SectionAlongX { get; set; } = true;

    /// <summary>section mode: look at the other side of the cutting line.</summary>
    [JsonProperty("flip")]
    public bool Flip { get; set; }
}

public class DetailViewCreationResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("viewId")]
    public long ViewId { get; set; }

    [JsonProperty("viewUniqueId")]
    public string ViewUniqueId { get; set; } = string.Empty;

    [JsonProperty("viewName")]
    public string ViewName { get; set; } = string.Empty;

    [JsonProperty("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonProperty("scale")]
    public int Scale { get; set; }

    /// <summary>
    ///     section mode: the direction the section looks, as a unit vector. Reported so a wrong
    ///     side is visible immediately and can be fixed with flip.
    /// </summary>
    [JsonProperty("lookDirection")]
    public List<double> LookDirection { get; set; }

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new List<string>();
}

/// <summary>
///     One detail component to place: point-based, or line-based when endPoint is provided
/// </summary>
public class DetailComponentItemInfo
{
    [JsonProperty("familyName")]
    public string FamilyName { get; set; } = string.Empty;

    [JsonProperty("typeName")]
    public string TypeName { get; set; } = string.Empty;

    /// <summary>
    ///     Placement point (line start for line-based components), model coordinates, mm
    /// </summary>
    [JsonProperty("point")]
    public JZPoint Point { get; set; }

    /// <summary>
    ///     Line end for line-based detail components, mm
    /// </summary>
    [JsonProperty("endPoint")]
    public JZPoint EndPoint { get; set; }

    /// <summary>
    ///     Rotation around the view direction at the placement point, degrees
    /// </summary>
    [JsonProperty("rotation")]
    public double Rotation { get; set; }
}

public class DetailComponentPlacementInfo
{
    [JsonProperty("viewId")]
    public long ViewId { get; set; }

    [JsonProperty("viewUniqueId")]
    public string ViewUniqueId { get; set; } = string.Empty;

    [JsonProperty("viewName")]
    public string ViewName { get; set; } = string.Empty;

    [JsonProperty("items")]
    public List<DetailComponentItemInfo> Items { get; set; } = new List<DetailComponentItemInfo>();
}

public class DetailComponentPlacedItem
{
    [JsonProperty("placed")]
    public bool Placed { get; set; }

    [JsonProperty("elementId")]
    public long ElementId { get; set; }

    [JsonProperty("elementUniqueId")]
    public string ElementUniqueId { get; set; } = string.Empty;

    [JsonProperty("familyName")]
    public string FamilyName { get; set; } = string.Empty;

    [JsonProperty("typeName")]
    public string TypeName { get; set; } = string.Empty;

    [JsonProperty("warning")]
    public string Warning { get; set; } = string.Empty;
}

public class DetailComponentPlacementResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("viewId")]
    public long ViewId { get; set; }

    [JsonProperty("viewName")]
    public string ViewName { get; set; } = string.Empty;

    [JsonProperty("placedCount")]
    public int PlacedCount { get; set; }

    [JsonProperty("items")]
    public List<DetailComponentPlacedItem> Items { get; set; } = new List<DetailComponentPlacedItem>();

    /// <summary>
    ///     Detail component types available in the project ('Family: Type'), filled when
    ///     a requested type could not be resolved
    /// </summary>
    [JsonProperty("availableTypes")]
    public List<string> AvailableTypes { get; set; } = new List<string>();

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new List<string>();
}

/// <summary>
///     Information for text note creation (annotation callouts on detail views)
/// </summary>
public class TextNoteCreationInfo
{
    [JsonProperty("viewId")]
    public long ViewId { get; set; }

    [JsonProperty("viewUniqueId")]
    public string ViewUniqueId { get; set; } = string.Empty;

    [JsonProperty("viewName")]
    public string ViewName { get; set; } = string.Empty;

    [JsonProperty("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    ///     Text position (model/view coordinates, mm)
    /// </summary>
    [JsonProperty("position")]
    public JZPoint Position { get; set; }

    [JsonProperty("textTypeName")]
    public string TextTypeName { get; set; } = string.Empty;

    /// <summary>
    ///     Wrapping width, mm; 0 places an unwrapped note
    /// </summary>
    [JsonProperty("width")]
    public double Width { get; set; }

    /// <summary>
    ///     Leader end point (arrow), mm; a straight leader is added when provided
    /// </summary>
    [JsonProperty("leaderEnd")]
    public JZPoint LeaderEnd { get; set; }
}

public class DetailPolylineInfo
{
    [JsonProperty("points")]
    public List<DetailLinePoint> Points { get; set; } = new List<DetailLinePoint>();

    /// <summary>
    ///     Line style (OST_Lines subcategory) for this polyline; falls back to the call-level
    ///     lineStyleName, then to the view default.
    /// </summary>
    [JsonProperty("lineStyleName")]
    public string LineStyleName { get; set; } = string.Empty;

    /// <summary>Closes the contour back to the first point.</summary>
    [JsonProperty("closed")]
    public bool Closed { get; set; }
}

/// <summary>
///     Arc through three points. Revit builds an arc from its two endpoints plus a point lying on
///     it, which is also how a rounded corner is described on a node.
/// </summary>
public class DetailArcInfo
{
    [JsonProperty("start")]
    public DetailLinePoint Start { get; set; }

    [JsonProperty("end")]
    public DetailLinePoint End { get; set; }

    [JsonProperty("pointOnArc")]
    public DetailLinePoint PointOnArc { get; set; }

    [JsonProperty("lineStyleName")]
    public string LineStyleName { get; set; } = string.Empty;
}

public class DetailLinePoint
{
    [JsonProperty("x")]
    public double X { get; set; }

    [JsonProperty("y")]
    public double Y { get; set; }
}

public class DetailLinesCreationInfo
{
    [JsonProperty("viewId")]
    public long ViewId { get; set; }

    [JsonProperty("viewUniqueId")]
    public string ViewUniqueId { get; set; } = string.Empty;

    [JsonProperty("viewName")]
    public string ViewName { get; set; } = string.Empty;

    [JsonProperty("polylines")]
    public List<DetailPolylineInfo> Polylines { get; set; } = new List<DetailPolylineInfo>();

    [JsonProperty("arcs")]
    public List<DetailArcInfo> Arcs { get; set; } = new List<DetailArcInfo>();

    /// <summary>Default line style (OST_Lines subcategory) for everything drawn by this call.</summary>
    [JsonProperty("lineStyleName")]
    public string LineStyleName { get; set; } = string.Empty;
}

public class TextNoteCreationResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("textNoteId")]
    public long TextNoteId { get; set; }

    [JsonProperty("textNoteUniqueId")]
    public string TextNoteUniqueId { get; set; } = string.Empty;

    [JsonProperty("viewId")]
    public long ViewId { get; set; }

    [JsonProperty("textType")]
    public string TextType { get; set; } = string.Empty;

    [JsonProperty("hasLeader")]
    public bool HasLeader { get; set; }

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new List<string>();
}
