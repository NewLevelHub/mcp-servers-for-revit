using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Detailing;
using RevitMCPCommandSet.Utils;
using RevitMCPCommandSet.Utils.Detailing;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Detailing;

/// <summary>
///     Generates a construction node from the layered build-up Revit already knows.
///     <para>
///     A junction detail is not invented: the wall type and the floor type carry their layers,
///     thicknesses and materials, and the materials carry their cut patterns. Reading them and
///     drawing them is deterministic work. What stays with the architect is deciding which node
///     belongs here and signing it off.
///     </para>
/// </summary>
public class CreateNodeDetailEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private NodeDetailCreationInfo _info;
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public NodeDetailCreationResult ResultInfo { get; private set; } = new NodeDetailCreationResult();
    public bool TaskCompleted { get; private set; }

    public void SetParameters(NodeDetailCreationInfo info)
    {
        _info = info ?? new NodeDetailCreationInfo();
        TaskCompleted = false;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 120000)
    {
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = app.ActiveUIDocument.Document;
            ResultInfo = Create(doc, _info);

            if (ResultInfo.Success && _info.ActivateView && ResultInfo.ViewId > 0)
            {
                if (doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(ResultInfo.ViewId)) is View view &&
                    !view.IsTemplate)
                {
                    app.ActiveUIDocument.ActiveView = view;
                }
            }
        }
        catch (Exception ex)
        {
            ResultInfo = new NodeDetailCreationResult
            {
                Success = false,
                Message = $"Error creating node detail: {ex.Message}"
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public string GetName() => "Create Node Detail";

    public static NodeDetailCreationResult Create(Document doc, NodeDetailCreationInfo info)
    {
        if (doc == null)
            throw new ArgumentNullException(nameof(doc));

        info ??= new NodeDetailCreationInfo();
        var mode = (info.Mode ?? "junction").Trim().ToLowerInvariant();
        var scale = info.Scale > 0 ? info.Scale : 10;

        var result = new NodeDetailCreationResult
        {
            Mode = mode,
            Scale = scale
        };

        var request = new NodeDetailRequest
        {
            Mode = mode,
            Orientation = info.Orientation,
            LengthMm = info.LengthMm,
            WallRunMm = info.WallRunMm,
            GapMm = info.GapMm,
            Scale = scale,
            Annotate = info.Annotate
        };

        var hatches = new Dictionary<string, LayerHatch>(StringComparer.Ordinal);

        var wallStructure = ReadAssembly(doc, info.WallTypeId, "wallTypeId");
        var floorStructure = ReadAssembly(doc, info.FloorTypeId, "floorTypeId");

        if (mode == "junction")
        {
            if (wallStructure == null || floorStructure == null)
            {
                throw new ArgumentException(
                    "Junction mode needs both wallTypeId and floorTypeId (a type id or an instance id). " +
                    "Use mode 'single' to draw one build-up.");
            }

            result.Wall = Describe(doc, wallStructure, hatches, "wall", result);
            result.Floor = Describe(doc, floorStructure, hatches, "floor", result);
            request.PrimaryLayers = ToSpecs(wallStructure, hatches, "wall");
            request.SecondaryLayers = ToSpecs(floorStructure, hatches, "floor");
        }
        else
        {
            var single = wallStructure ?? floorStructure
                ?? throw new ArgumentException(
                    "Pass wallTypeId or floorTypeId — a type id, or the id of a wall/floor instance.");

            var role = wallStructure != null ? "wall" : "floor";
            var report = Describe(doc, single, hatches, role, result);

            if (role == "wall")
            {
                result.Wall = report;
                // A wall in section stacks left to right unless the caller says otherwise.
                if (string.IsNullOrWhiteSpace(info.Orientation))
                    request.Orientation = "vertical";
            }
            else
            {
                result.Floor = report;
            }

            request.PrimaryLayers = ToSpecs(single, hatches, role);
        }

        ApplyExtraLayers(doc, info, request, mode, hatches, result);

        result.LayersRead = request.PrimaryLayers.Count + request.SecondaryLayers.Count;

        var geometry = NodeDetailGeometryBuilder.Build(request);
        result.Warnings.AddRange(geometry.Warnings);

        var view = CreateDraftingView(doc, info, scale, mode);
        result.ViewId = view.Id.GetValue();
        result.ViewUniqueId = view.UniqueId;
        result.ViewName = view.Name;

        Draw(doc, view, geometry, info, hatches, result);

        if (!string.IsNullOrWhiteSpace(info.SheetNumber))
            PlaceOnSheet(doc, view, info.SheetNumber.Trim(), result);

        result.Success = result.CurvesCreated > 0;
        result.Message = result.Success
            ? $"Node '{view.Name}' generated from {result.LayersRead} layers: " +
              $"{result.RegionsCreated} hatched areas, {result.CurvesCreated} curves, " +
              $"{result.DimensionsCreated} dimensions, {result.NotesCreated} notes."
            : $"Node view '{view.Name}' was created but nothing could be drawn on it.";

        return result;
    }

    // ------------------------------------------------------------- reading

    private static AssemblyStructureInfo ReadAssembly(Document doc, long elementId, string parameterName)
    {
        if (elementId <= 0)
            return null;

        var hostType = CompoundStructureReader.ResolveHostType(doc, elementId)
            ?? throw new ArgumentException(
                $"{parameterName}={elementId} is not a wall/floor/roof/ceiling type or instance. " +
                "Call get_available_family_types or pass the id of a placed element.");

        return CompoundStructureReader.Read(doc, hostType);
    }

    private static NodeAssemblyReport Describe(
        Document doc,
        AssemblyStructureInfo structure,
        IDictionary<string, LayerHatch> hatches,
        string role,
        NodeDetailCreationResult result)
    {
        var report = new NodeAssemblyReport
        {
            TypeId = structure.TypeId,
            TypeName = structure.TypeName,
            Category = structure.Category,
            TotalThicknessMm = structure.TotalThicknessMm
        };

        foreach (var warning in structure.Warnings)
            result.Warnings.Add($"{structure.TypeName}: {warning}");

        foreach (var layer in structure.Layers)
        {
            var hatch = LayerHatchResolver.Resolve(doc, layer, null);
            hatches[HatchKey(role, layer.Index)] = hatch;

            if (hatch.Pattern == null && !layer.IsZeroWidth)
                result.LayersWithoutHatch++;

            report.Layers.Add(new NodeLayerReport
            {
                Index = layer.Index,
                Name = string.IsNullOrWhiteSpace(layer.MaterialName) ? layer.Function : layer.MaterialName,
                ThicknessMm = layer.ThicknessMm,
                Function = layer.Function,
                HatchPattern = hatch.PatternName,
                HatchSource = hatch.Source
            });
        }

        return report;
    }

    private static List<NodeLayerSpec> ToSpecs(
        AssemblyStructureInfo structure,
        IReadOnlyDictionary<string, LayerHatch> hatches,
        string role)
    {
        return structure.Layers
            .Select(layer => new NodeLayerSpec
            {
                Name = string.IsNullOrWhiteSpace(layer.MaterialName) ? layer.Function : layer.MaterialName,
                ThicknessMm = layer.ThicknessMm,
                Function = layer.Function,
                IsZeroWidth = layer.IsZeroWidth,
                IsCore = layer.IsCore,
                HatchPattern = HatchKey(role, layer.Index)
            })
            .ToList();
    }

    /// <summary>
    ///     Layers the model does not carry. They are drawn like any other layer but their hatch is
    ///     resolved from the request, since there is no material to ask.
    /// </summary>
    private static void ApplyExtraLayers(
        Document doc,
        NodeDetailCreationInfo info,
        NodeDetailRequest request,
        string mode,
        IDictionary<string, LayerHatch> hatches,
        NodeDetailCreationResult result)
    {
        var extras = info.ExtraLayers ?? new List<NodeExtraLayerInfo>();
        var counter = 0;

        foreach (var extra in extras)
        {
            if (extra == null || extra.ThicknessMm < 0)
                continue;

            var toFloor = mode == "junction" &&
                          !"wall".Equals((extra.Target ?? "floor").Trim(), StringComparison.OrdinalIgnoreCase);
            var target = toFloor ? request.SecondaryLayers : request.PrimaryLayers;

            var key = HatchKey("extra", counter++);
            var pseudoLayer = new AssemblyLayerInfo { Function = extra.Function ?? string.Empty };
            var hatch = LayerHatchResolver.Resolve(doc, pseudoLayer, extra.FillPatternName);
            hatches[key] = hatch;

            if (hatch.Pattern == null && extra.ThicknessMm > 0.01)
            {
                result.LayersWithoutHatch++;
                result.Warnings.Add(
                    $"Extra layer '{extra.Name}': no hatch resolved " +
                    $"(fillPatternName='{extra.FillPatternName}', function='{extra.Function}').");
            }

            var spec = new NodeLayerSpec
            {
                Name = extra.Name ?? string.Empty,
                ThicknessMm = extra.ThicknessMm,
                Function = extra.Function ?? string.Empty,
                IsZeroWidth = extra.ThicknessMm <= 0.01,
                HatchPattern = key
            };

            var at = extra.InsertAt;
            if (at < 0 || at > target.Count)
                target.Add(spec);
            else
                target.Insert(at, spec);
        }
    }

    private static string HatchKey(string role, int index) => $"{role}#{index}";

    // ------------------------------------------------------------- drawing

    private static View CreateDraftingView(Document doc, NodeDetailCreationInfo info, int scale, string mode)
    {
        var name = string.IsNullOrWhiteSpace(info.Name)
            ? (mode == "junction" ? "Узел. Примыкание" : "Узел. Разрез")
            : info.Name.Trim();

        var created = CreateDetailViewEventHandler.Create(doc, new DetailViewCreationInfo
        {
            Mode = "drafting",
            Name = name,
            Scale = scale,
            DetailLevel = "Fine",
            ActivateView = false
        });

        return doc.GetElement(created.ViewUniqueId) as View
            ?? throw new InvalidOperationException("The drafting view for the node could not be created.");
    }

    private static void Draw(
        Document doc,
        View view,
        NodeDetailGeometry geometry,
        NodeDetailCreationInfo info,
        IReadOnlyDictionary<string, LayerHatch> hatches,
        NodeDetailCreationResult result)
    {
        var z = DetailDrawing.ViewPlaneZ(view);
        var textTypeId = ResolveTextType(doc, info.TextTypeName, result);
        var dimensionType = ResolveDimensionType(doc, info.DimensionTypeName, result);
        var lineStyle = DetailDrawing.ResolveLineStyle(doc, info.LineStyleName);

        if (!string.IsNullOrWhiteSpace(info.LineStyleName) && lineStyle == null)
            result.Warnings.Add($"Line style '{info.LineStyleName}' was not found; the view default is used.");

        using var tx = new Transaction(doc, "MCP Create Node Detail");
        tx.Start();

        if (info.DrawHatches)
            DrawHatches(doc, view, geometry, hatches, info.CreateMissingTypes, z, result);

        var segmentCurves = DrawSegments(doc, view, geometry, lineStyle, z, result);

        if (info.Annotate)
        {
            DrawDimensions(doc, view, geometry, segmentCurves, dimensionType, z, result);
            DrawNotes(doc, view, geometry, textTypeId, z, result);
        }

        tx.Commit();
    }

    private static void DrawHatches(
        Document doc,
        View view,
        NodeDetailGeometry geometry,
        IReadOnlyDictionary<string, LayerHatch> hatches,
        bool createMissingTypes,
        double z,
        NodeDetailCreationResult result)
    {
        var typeCache = new Dictionary<long, FilledRegionType>();

        foreach (var contour in geometry.Contours)
        {
            if (!hatches.TryGetValue(contour.HatchPattern ?? string.Empty, out var hatch) || hatch.Pattern == null)
                continue;

            try
            {
                var regionType = ResolveRegionType(doc, hatch, createMissingTypes, typeCache, result);
                if (regionType == null)
                    continue;

                var loop = DetailDrawing.BuildClosedLoop(
                    contour.Points.Select(point => DetailDrawing.ToViewPoint(point.X, point.Y, z)).ToList());

                var region = DetailDrawing.FillContour(doc, view, new List<CurveLoop> { loop }, regionType.Id);

                var comments = region.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (comments != null && !comments.IsReadOnly)
                    comments.Set($"{CreateDetailRegionsEventHandler.DefaultCommentTag} {contour.Label}");

                result.RegionsCreated++;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Hatch for '{contour.Label}' failed: {ex.Message}");
            }
        }
    }

    private static FilledRegionType ResolveRegionType(
        Document doc,
        LayerHatch hatch,
        bool createMissingTypes,
        IDictionary<long, FilledRegionType> cache,
        NodeDetailCreationResult result)
    {
        var patternId = hatch.Pattern.Id.GetValue();
        if (cache.TryGetValue(patternId, out var cached))
            return cached;

        var existing = FilledRegionTypes.FindByPattern(doc, hatch.Pattern.Id);
        if (existing == null && createMissingTypes)
        {
            existing = FilledRegionTypes.EnsureForPattern(doc, hatch.Pattern, null, out var created);
            if (created && existing != null)
                result.CreatedFilledRegionTypes.Add(existing.Name);
        }

        if (existing == null)
        {
            result.Warnings.Add(
                $"No filled region type draws with '{hatch.Pattern.Name}' and none could be created.");
            return null;
        }

        cache[patternId] = existing;
        return existing;
    }

    /// <summary>
    ///     Returns the created curve for each geometry segment, by index. Dimensions attach to these
    ///     exact curves rather than hunting for references near a coordinate.
    /// </summary>
    private static Dictionary<int, DetailCurve> DrawSegments(
        Document doc,
        View view,
        NodeDetailGeometry geometry,
        GraphicsStyle lineStyle,
        double z,
        NodeDetailCreationResult result)
    {
        var curves = new Dictionary<int, DetailCurve>();

        for (var i = 0; i < geometry.Segments.Count; i++)
        {
            var segment = geometry.Segments[i];

            try
            {
                var curve = DetailDrawing.DrawSegment(
                    doc,
                    view,
                    DetailDrawing.ToViewPoint(segment.Start.X, segment.Start.Y, z),
                    DetailDrawing.ToViewPoint(segment.End.X, segment.End.Y, z));

                if (curve == null)
                    continue;

                curves[i] = curve;
                result.CurvesCreated++;

                if (lineStyle != null)
                    DetailDrawing.TryApplyLineStyle(curve, lineStyle, out _);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Segment {i} ({segment.Role}) failed: {ex.Message}");
            }
        }

        return curves;
    }

    private static void DrawDimensions(
        Document doc,
        View view,
        NodeDetailGeometry geometry,
        IReadOnlyDictionary<int, DetailCurve> segmentCurves,
        DimensionType dimensionType,
        double z,
        NodeDetailCreationResult result)
    {
        foreach (var spec in geometry.Dimensions)
        {
            try
            {
                var references = new ReferenceArray();
                var used = 0;

                foreach (var index in spec.SegmentIndices)
                {
                    if (!segmentCurves.TryGetValue(index, out var curve))
                        continue;

                    var reference = curve.GeometryCurve?.Reference;
                    if (reference == null)
                        continue;

                    references.Append(reference);
                    used++;
                }

                if (used < 2)
                {
                    result.Warnings.Add(
                        $"Dimension '{spec.Label}' skipped: only {used} of {spec.SegmentIndices.Count} " +
                        "layer boundaries produced a reference.");
                    continue;
                }

                var line = Line.CreateBound(
                    DetailDrawing.ToViewPoint(spec.LineStart.X, spec.LineStart.Y, z),
                    DetailDrawing.ToViewPoint(spec.LineEnd.X, spec.LineEnd.Y, z));

                var dimension = dimensionType != null
                    ? doc.Create.NewDimension(view, line, references, dimensionType)
                    : doc.Create.NewDimension(view, line, references);

                if (dimension != null)
                    result.DimensionsCreated++;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Dimension '{spec.Label}' failed: {ex.Message}");
            }
        }
    }

    private static void DrawNotes(
        Document doc,
        View view,
        NodeDetailGeometry geometry,
        ElementId textTypeId,
        double z,
        NodeDetailCreationResult result)
    {
        if (textTypeId == null || textTypeId == ElementId.InvalidElementId)
            return;

        foreach (var spec in geometry.Notes)
        {
            try
            {
                var position = DetailDrawing.ToViewPoint(spec.Position.X, spec.Position.Y, z);

                var options = new TextNoteOptions { TypeId = textTypeId };
                if (spec.AlignRight)
                {
                    // The anchor is the right edge, so a label left of the node grows away from it.
                    options.HorizontalAlignment = HorizontalTextAlignment.Right;
                }

                var note = TextNote.Create(doc, view.Id, position, spec.Text, options);

                if (spec.LeaderEnd != null)
                {
                    var leaderEnd = DetailDrawing.ToViewPoint(spec.LeaderEnd.X, spec.LeaderEnd.Y, z);
                    var leader = note.AddLeader(leaderEnd.X <= position.X
                        ? TextNoteLeaderTypes.TNLT_STRAIGHT_L
                        : TextNoteLeaderTypes.TNLT_STRAIGHT_R);
                    leader.End = leaderEnd;
                }

                result.NotesCreated++;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Note '{spec.Text}' failed: {ex.Message}");
            }
        }
    }

    private static void PlaceOnSheet(Document doc, View view, string sheetNumber, NodeDetailCreationResult result)
    {
        var sheet = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .FirstOrDefault(candidate =>
                candidate.SheetNumber.Equals(sheetNumber, StringComparison.OrdinalIgnoreCase));

        if (sheet == null)
        {
            result.Warnings.Add(
                $"Sheet '{sheetNumber}' was not found. Create it with create_sheet, " +
                "then place the node with place_view_on_sheet.");
            return;
        }

        try
        {
            using var tx = new Transaction(doc, "MCP Place Node On Sheet");
            tx.Start();

            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
            {
                result.Warnings.Add($"View '{view.Name}' cannot be added to sheet '{sheetNumber}'.");
                tx.RollBack();
                return;
            }

            var outline = sheet.Outline;
            var center = new XYZ(
                (outline.Min.U + outline.Max.U) / 2,
                (outline.Min.V + outline.Max.V) / 2,
                0);

            Viewport.Create(doc, sheet.Id, view.Id, center);
            tx.Commit();

            result.SheetId = sheet.Id.GetValue();
            result.SheetNumber = sheet.SheetNumber;
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Placing the node on sheet '{sheetNumber}' failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------- lookups

    private static ElementId ResolveTextType(Document doc, string typeName, NodeDetailCreationResult result)
    {
        var types = new FilteredElementCollector(doc)
            .OfClass(typeof(TextNoteType))
            .Cast<TextNoteType>()
            .ToList();

        if (types.Count == 0)
        {
            result.Warnings.Add("The project has no text note types; layer labels were not written.");
            return ElementId.InvalidElementId;
        }

        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var byName = types.FirstOrDefault(type =>
                type.Name.Equals(typeName.Trim(), StringComparison.OrdinalIgnoreCase));

            if (byName != null)
                return byName.Id;

            result.Warnings.Add($"Text note type '{typeName}' was not found; the default text type is used.");
        }

        var defaultTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        return defaultTypeId != ElementId.InvalidElementId && doc.GetElement(defaultTypeId) is TextNoteType
            ? defaultTypeId
            : types[0].Id;
    }

    private static DimensionType ResolveDimensionType(
        Document doc,
        string typeName,
        NodeDetailCreationResult result)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        var types = new FilteredElementCollector(doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .Where(type => type.StyleType == DimensionStyleType.Linear)
            .ToList();

        var match = types.FirstOrDefault(type =>
                        type.Name.Equals(typeName.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? types.FirstOrDefault(type =>
                        type.Name.IndexOf(typeName.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);

        if (match == null)
            result.Warnings.Add($"Dimension type '{typeName}' was not found; the project default is used.");

        return match;
    }
}
