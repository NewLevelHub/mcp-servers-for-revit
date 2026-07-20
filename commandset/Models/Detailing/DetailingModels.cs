using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.Detailing;

/// <summary>
///     Information for detail view creation (callout of a model view or a drafting view)
/// </summary>
public class DetailViewCreationInfo
{
    /// <summary>
    ///     callout — detail callout of a parent model view; drafting — independent drafting view
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
