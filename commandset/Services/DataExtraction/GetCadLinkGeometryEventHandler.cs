using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    /// <summary>
    /// Reads line/arc/polyline geometry from ImportInstance (DWG CAD link/import) in mm (REV-138).
    /// </summary>
    public class GetCadLinkGeometryEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private string _cadLinkName = string.Empty;
        private List<string> _layerFilters = new List<string>();
        private long _viewId;
        private double _minLengthMm;
        private int _limit;
        private bool _includeHiddenLayers;
        private bool _includeModelLines;
        /// <summary>REV-149: tessellate (chords, default) | single (one chord per arc + arc metadata).</summary>
        private string _arcMode = "tessellate";
        private List<CadBlockItem> _blocks = new List<CadBlockItem>();

        public GetCadLinkGeometryResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        /// <summary>REV-149: arc center/radius/endpoints in model space, shared by every chord.</summary>
        private sealed class ArcInfo
        {
            public string Id;
            public CadPointMm CenterMm;
            public double RadiusMm;
            public double StartAngleDeg;
            public double EndAngleDeg;
        }

        public void SetParameters(
            string cadLinkName = "",
            IEnumerable<string> layerFilters = null,
            long viewId = 0,
            double minLengthMm = 0,
            int limit = 5000,
            bool includeHiddenLayers = false,
            bool includeModelLines = false,
            string arcMode = "tessellate")
        {
            _cadLinkName = cadLinkName ?? string.Empty;
            _layerFilters = (layerFilters ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList();
            _viewId = viewId;
            _minLengthMm = Math.Max(0, minLengthMm);
            _limit = limit > 0 ? limit : 5000;
            _includeHiddenLayers = includeHiddenLayers;
            _includeModelLines = includeModelLines;
            _arcMode = string.Equals(arcMode, "single", StringComparison.OrdinalIgnoreCase)
                ? "single"
                : "tessellate";
            _blocks = new List<CadBlockItem>();
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            // Do not Reset here - SetParameters/Prepare already Reset before Raise.
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var uiDoc = app.ActiveUIDocument;
                if (uiDoc == null)
                {
                    ResultInfo = Fail("Нет активного документа. Откройте проект Revit.");
                    return;
                }

                var doc = uiDoc.Document;
                var view = ResolveView(doc, uiDoc);
                if (view == null)
                {
                    ResultInfo = Fail("Не удалось определить вид. Откройте план этажа с привязанным DWG.");
                    return;
                }

                var importsOnView = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(ImportInstance))
                    .WhereElementIsNotElementType()
                    .Cast<ImportInstance>()
                    .ToList();

                var allInDoc = new FilteredElementCollector(doc)
                    .OfClass(typeof(ImportInstance))
                    .WhereElementIsNotElementType()
                    .Cast<ImportInstance>()
                    .ToList();

                var selected = FilterByName(doc, importsOnView, _cadLinkName);

                // Exploded DWG often leaves wall faces as Model/Detail lines, while only
                // block leftovers remain as ImportInstance. Allow model-line fallback.
                if (importsOnView.Count == 0 && !_includeModelLines)
                {
                    var hint = allInDoc.Count == 0
                        ? "На виде нет CAD/DWG. Привяжите DWG к уровню (Вставка → Связь CAD) и откройте план этажа."
                        : $"На виде «{view.Name}» нет видимых CAD-подложек (в проекте есть {allInDoc.Count}). " +
                          "Покажите связь на этом плане или привяжите DWG к уровню вида. " +
                          "Если DWG взорван — вызовите с includeModelLines=true.";

                    ResultInfo = Fail(hint, view, BuildLinkInfos(doc, allInDoc));
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_cadLinkName) && selected.Count == 0 && !_includeModelLines)
                {
                    var available = string.Join(", ",
                        importsOnView.Select(i => $"«{GetCadName(doc, i)}»"));
                    ResultInfo = Fail(
                        $"CAD «{_cadLinkName}» не найден на виде «{view.Name}». Доступны: {available}.",
                        view,
                        BuildLinkInfos(doc, importsOnView));
                    return;
                }

                var options = new Options
                {
                    ComputeReferences = false,
                    IncludeNonVisibleObjects = true,
                    View = view
                };

                var items = new List<CadSegmentItem>();
                var linkInfos = new List<CadLinkInfo>();
                bool truncated = false;
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                bool hasBbox = false;

                foreach (var import in selected)
                {
                    if (truncated)
                        break;

                    var linkName = GetCadName(doc, import);
                    var linkId = import.Id.GetValue();
                    int before = items.Count;
                    int geomIndex = 0;

                    var geometry = import.get_Geometry(options);
                    if (geometry == null)
                        continue;

                    // REV-149: an exploded DWG turns every block into its own ImportInstance,
                    // so the import transform IS the block placement (insert point + rotation
                    // + mirror). Record it before walking the curves.
                    int importBlockIndex = RegisterBlock(
                        linkName, SafeImportTransform(import), linkId, "importInstance");

                    foreach (GeometryObject top in geometry)
                    {
                        if (truncated)
                            break;

                        AppendGeometryObject(
                            doc, view, top, linkName, linkId, importBlockIndex, ref geomIndex,
                            items, ref truncated, ref hasBbox,
                            ref minX, ref minY, ref maxX, ref maxY);
                    }

                    linkInfos.Add(new CadLinkInfo
                    {
                        ElementId = linkId,
                        Name = linkName,
                        IsLinked = import.IsLinked,
                        SegmentCount = items.Count - before
                    });
                }

                int modelLineCount = 0;
                if (_includeModelLines && !truncated)
                {
                    modelLineCount = AppendModelAndDetailLines(
                        doc, view, items, ref truncated, ref hasBbox,
                        ref minX, ref minY, ref maxX, ref maxY);
                    if (modelLineCount > 0)
                    {
                        linkInfos.Add(new CadLinkInfo
                        {
                            ElementId = 0,
                            Name = "modelLines/detailLines",
                            IsLinked = false,
                            SegmentCount = modelLineCount
                        });
                    }
                }

                if (items.Count == 0)
                {
                    var hint = _includeModelLines
                        ? "Нет линий ImportInstance и Model/Detail Lines (или все отфильтрованы по слою/длине)."
                        : "CAD найден, но линий/дуг/полилиний нет (или все отфильтрованы по слою/длине). " +
                          "Если DWG взорван в линии модели — повторите с includeModelLines=true.";
                    ResultInfo = Fail(hint, view, BuildLinkInfos(doc, selected.Count > 0 ? selected : importsOnView));
                    return;
                }

                var primary = selected.Count > 0 ? selected[0] : null;
                var primaryName = primary != null ? GetCadName(doc, primary) : (modelLineCount > 0 ? "modelLines" : null);
                var layers = items.Select(i => i.Layer).Where(l => !string.IsNullOrEmpty(l)).Distinct().Take(8);
                var layerHint = string.Join("/", layers);

                var layerSummary = items
                    .GroupBy(i => i.Layer ?? string.Empty)
                    .Select(g => new CadLayerSummaryItem { Layer = g.Key, Count = g.Count() })
                    .OrderByDescending(x => x.Count)
                    .Take(24)
                    .ToList();

                var sourceHint = modelLineCount > 0
                    ? $", model/detail lines {modelLineCount}"
                    : "";

                // REV-149: keep only blocks that actually produced geometry; Index stays the
                // value stored on the segments, so it is a lookup key, not a list position.
                var blocks = _blocks.Where(b => b.SegmentCount > 0).ToList();
                var blockHint = blocks.Count > 0 ? $", блоков {blocks.Count}" : "";

                ResultInfo = new GetCadLinkGeometryResult
                {
                    Ok = true,
                    Summary = selected.Count <= 1
                        ? $"{(primaryName != null ? $"DWG «{primaryName}»" : "Линии")}: {items.Count} сегмент(ов)" +
                          (string.IsNullOrEmpty(layerHint) ? "" : $", слои {layerHint}") +
                          sourceHint + blockHint +
                          (truncated ? " (урезано)" : "")
                        : $"CAD ×{selected.Count}: {items.Count} сегмент(ов)" + sourceHint + blockHint + (truncated ? " (урезано)" : ""),
                    Count = items.Count,
                    Items = items,
                    BboxMm = hasBbox
                        ? new CadBboxMm { MinX = Round1(minX), MinY = Round1(minY), MaxX = Round1(maxX), MaxY = Round1(maxY) }
                        : null,
                    CadLinkName = selected.Count == 1 ? primaryName : (selected.Count == 0 && modelLineCount > 0 ? "modelLines" : null),
                    CadLinkElementId = selected.Count == 1 ? primary!.Id.GetValue() : null,
                    ImportUnits = "mm",
                    ViewId = view.Id.GetValue(),
                    ViewName = view.Name,
                    AvailableLinks = linkInfos,
                    Truncated = truncated,
                    Message = truncated
                        ? $"Показаны первые {_limit} сегментов. Уточните layerFilter или увеличьте limit."
                        : null,
                    LayerSummary = layerSummary,
                    Blocks = blocks
                };
            }
            catch (Exception ex)
            {
                ResultInfo = Fail($"Не удалось прочитать геометрию CAD: {ex.Message}");
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private void AppendGeometryObject(
            Document doc,
            View view,
            GeometryObject obj,
            string linkName,
            long linkId,
            int blockIndex,
            ref int geomIndex,
            List<CadSegmentItem> items,
            ref bool truncated,
            ref bool hasBbox,
            ref double minX,
            ref double minY,
            ref double maxX,
            ref double maxY)
        {
            if (obj == null || truncated)
                return;

            if (obj is GeometryInstance gi)
            {
                // REV-149: a nested instance is a block inside the DWG — record its placement.
                int nestedIndex = RegisterBlock(
                    ResolveInstanceName(doc, gi, linkName), gi.Transform, linkId, "nested");

                var instanceGeom = gi.GetInstanceGeometry();
                bool instanceHasCurves = GeometryElementHasDrawableCurves(instanceGeom);

                if (instanceHasCurves)
                {
                    foreach (GeometryObject child in instanceGeom)
                    {
                        if (truncated)
                            break;
                        AppendGeometryObject(
                            doc, view, child, linkName, linkId, nestedIndex, ref geomIndex,
                            items, ref truncated, ref hasBbox,
                            ref minX, ref minY, ref maxX, ref maxY);
                    }
                }
                else
                {
                    // Some exploded blocks expose geometry only via symbol + transform.
                    var symbolGeom = gi.GetSymbolGeometry();
                    if (symbolGeom != null)
                    {
                        var transform = gi.Transform;
                        foreach (GeometryObject child in symbolGeom)
                        {
                            if (truncated)
                                break;
                            AppendTransformedGeometryObject(
                                doc, view, child, transform, linkName, linkId, nestedIndex, ref geomIndex,
                                items, ref truncated, ref hasBbox,
                                ref minX, ref minY, ref maxX, ref maxY);
                        }
                    }
                }

                return;
            }

            AppendObject(
                doc, view, obj, linkName, linkId, blockIndex, geomIndex++,
                items, ref truncated, ref hasBbox,
                ref minX, ref minY, ref maxX, ref maxY);
        }

        /// <summary>
        /// REV-149: store a block placement and return its index (-1 when the transform is unusable).
        /// </summary>
        private int RegisterBlock(string name, Transform transform, long linkId, string source)
        {
            if (transform == null)
                return -1;

            XYZ bx;
            XYZ by;
            XYZ origin;
            try
            {
                bx = transform.BasisX;
                by = transform.BasisY;
                origin = transform.Origin;
            }
            catch
            {
                return -1;
            }

            if (bx == null || by == null || origin == null)
                return -1;

            // 2D determinant < 0 → left-handed basis → the CAD symbol is mirrored.
            double det = (bx.X * by.Y) - (bx.Y * by.X);
            double rotationDeg = Math.Atan2(bx.Y, bx.X) * 180.0 / Math.PI;

            var block = new CadBlockItem
            {
                Index = _blocks.Count,
                Name = name ?? string.Empty,
                InsertMm = ToPointMm(origin),
                RotationDeg = Round1(rotationDeg),
                Mirrored = det < 0,
                CadLinkElementId = linkId,
                Source = source
            };
            _blocks.Add(block);
            return block.Index;
        }

        private static Transform SafeImportTransform(ImportInstance import)
        {
            try
            {
                return import?.GetTransform();
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveInstanceName(Document doc, GeometryInstance gi, string fallback)
        {
            try
            {
                // Revit 2025 dropped GeometryInstance.Symbol; the block element is now reached
                // through the id of its symbol geometry.
                using var symbolGeometryId = gi.GetSymbolGeometryId();
                var symbolId = symbolGeometryId?.SymbolId;

                if (symbolId != null && symbolId != ElementId.InvalidElementId)
                {
                    // Blocks nested in a linked DWG belong to the link's document, not the host.
                    var owner = gi.GetDocument() ?? doc;
                    var symbolName = owner?.GetElement(symbolId)?.Name;

                    if (!string.IsNullOrWhiteSpace(symbolName))
                        return symbolName;
                }
            }
            catch
            {
                // Imported symbols can throw; fall back to the link name.
            }

            return fallback ?? string.Empty;
        }

        private static bool GeometryElementHasDrawableCurves(GeometryElement geom)
        {
            if (geom == null)
                return false;

            foreach (GeometryObject obj in geom)
            {
                if (obj is Line || obj is PolyLine || obj is Arc || obj is Curve)
                    return true;
                if (obj is GeometryInstance nested)
                {
                    if (GeometryElementHasDrawableCurves(nested.GetInstanceGeometry()))
                        return true;
                }
            }

            return false;
        }

        private void AppendTransformedGeometryObject(
            Document doc,
            View view,
            GeometryObject obj,
            Transform transform,
            string linkName,
            long linkId,
            int blockIndex,
            ref int geomIndex,
            List<CadSegmentItem> items,
            ref bool truncated,
            ref bool hasBbox,
            ref double minX,
            ref double minY,
            ref double maxX,
            ref double maxY)
        {
            if (obj == null || truncated)
                return;

            if (obj is GeometryInstance gi)
            {
                var instanceGeom = gi.GetInstanceGeometry();
                if (instanceGeom != null)
                {
                    var combined = transform.Multiply(gi.Transform);
                    int nestedIndex = RegisterBlock(
                        ResolveInstanceName(doc, gi, linkName), combined, linkId, "nested");
                    foreach (GeometryObject child in instanceGeom)
                    {
                        if (truncated)
                            break;
                        AppendTransformedGeometryObject(
                            doc, view, child, combined, linkName, linkId, nestedIndex, ref geomIndex,
                            items, ref truncated, ref hasBbox,
                            ref minX, ref minY, ref maxX, ref maxY);
                    }
                }

                return;
            }

            var layer = ResolveLayer(doc, obj);
            if (!LayerMatches(layer))
                return;

            if (!_includeHiddenLayers && !IsLayerVisible(doc, view, obj))
                return;

            if (obj is PolyLine poly)
            {
                var coords = poly.GetCoordinates();
                for (int i = 0; i < coords.Count - 1; i++)
                {
                    if (!TryAddSegment(
                            items, transform.OfPoint(coords[i]), transform.OfPoint(coords[i + 1]),
                            layer, linkName, linkId, blockIndex,
                            $"{linkId}:{geomIndex}:pl{i}", "polylineSegment",
                            "importInstance", null,
                            ref truncated, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY))
                        return;
                }

                return;
            }

            if (obj is Line line)
            {
                TryAddSegment(
                    items, transform.OfPoint(line.GetEndPoint(0)), transform.OfPoint(line.GetEndPoint(1)),
                    layer, linkName, linkId, blockIndex,
                    $"{linkId}:{geomIndex}:ln", "line",
                    "importInstance", null,
                    ref truncated, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY);
                return;
            }

            if (obj is Arc || obj is Curve)
            {
                var c = obj as Curve;
                if (c == null)
                    return;

                AppendCurve(
                    c, transform, layer, linkName, linkId, blockIndex, geomIndex,
                    items, ref truncated, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY);
            }
        }

        /// <summary>
        /// REV-149: emit a curve as chords, attaching arc center/radius/endpoint angles so the
        /// client can rebuild the real arc (door swing → hinge + hand) instead of guessing.
        /// In arcMode=single an arc becomes ONE chord — tessellated chords of a small swing
        /// otherwise fall under minLengthMm and vanish.
        /// </summary>
        private void AppendCurve(
            Curve c,
            Transform transform,
            string layer,
            string linkName,
            long linkId,
            int blockIndex,
            int geomIndex,
            List<CadSegmentItem> items,
            ref bool truncated,
            ref bool hasBbox,
            ref double minX,
            ref double minY,
            ref double maxX,
            ref double maxY)
        {
            var arcInfo = BuildArcInfo(c, transform, linkId, geomIndex);
            var curveType = c is Arc ? "arc" : "curve";

            if (arcInfo != null && _arcMode == "single")
            {
                TryAddSegment(
                    items,
                    TransformPoint(transform, c.GetEndPoint(0)),
                    TransformPoint(transform, c.GetEndPoint(1)),
                    layer, linkName, linkId, blockIndex,
                    $"{linkId}:{geomIndex}:{curveType}", curveType,
                    "importInstance", arcInfo,
                    ref truncated, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY);
                return;
            }

            IList<XYZ> pts;
            try
            {
                pts = c.Tessellate();
            }
            catch
            {
                pts = new List<XYZ> { c.GetEndPoint(0), c.GetEndPoint(1) };
            }

            for (int i = 0; i < pts.Count - 1; i++)
            {
                if (!TryAddSegment(
                        items,
                        TransformPoint(transform, pts[i]),
                        TransformPoint(transform, pts[i + 1]),
                        layer, linkName, linkId, blockIndex,
                        $"{linkId}:{geomIndex}:{curveType}{i}", curveType,
                        "importInstance", arcInfo,
                        ref truncated, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY))
                    return;
            }
        }

        private static XYZ TransformPoint(Transform transform, XYZ p)
        {
            if (p == null)
                return null;
            return transform == null ? p : transform.OfPoint(p);
        }

        /// <summary>
        /// Arc center/radius/endpoint angles in model space. Endpoint angles (not the arc's own
        /// parameters) keep the result frame-independent after an arbitrary block transform.
        /// </summary>
        private static ArcInfo BuildArcInfo(Curve c, Transform transform, long linkId, int geomIndex)
        {
            if (c is not Arc arc)
                return null;

            try
            {
                XYZ center = TransformPoint(transform, arc.Center);
                XYZ p0 = TransformPoint(transform, arc.GetEndPoint(0));
                XYZ p1 = TransformPoint(transform, arc.GetEndPoint(1));
                if (center == null || p0 == null || p1 == null)
                    return null;

                double radiusMm = RevitUnitConversion.ToMillimeters(
                    new XYZ(p0.X - center.X, p0.Y - center.Y, 0).GetLength());
                if (radiusMm < 0.1)
                    return null;

                return new ArcInfo
                {
                    Id = $"{linkId}:{geomIndex}:arc",
                    CenterMm = ToPointMm(center),
                    RadiusMm = Round1(radiusMm),
                    StartAngleDeg = Round1(Math.Atan2(p0.Y - center.Y, p0.X - center.X) * 180.0 / Math.PI),
                    EndAngleDeg = Round1(Math.Atan2(p1.Y - center.Y, p1.X - center.X) * 180.0 / Math.PI)
                };
            }
            catch
            {
                return null;
            }
        }

        private void AppendObject(
            Document doc,
            View view,
            GeometryObject obj,
            string linkName,
            long linkId,
            int blockIndex,
            int geomIndex,
            List<CadSegmentItem> items,
            ref bool truncated,
            ref bool hasBbox,
            ref double minX,
            ref double minY,
            ref double maxX,
            ref double maxY)
        {
            if (obj == null || truncated)
                return;

            var layer = ResolveLayer(doc, obj);
            if (!LayerMatches(layer))
                return;

            if (!_includeHiddenLayers && !IsLayerVisible(doc, view, obj))
                return;

            if (obj is PolyLine poly)
            {
                var coords = poly.GetCoordinates();
                for (int i = 0; i < coords.Count - 1; i++)
                {
                    if (!TryAddSegment(
                            items, coords[i], coords[i + 1], layer, linkName, linkId, blockIndex,
                            $"{linkId}:{geomIndex}:pl{i}", "polylineSegment",
                            "importInstance", null,
                            ref truncated, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY))
                        return;
                }

                return;
            }

            if (obj is Line line)
            {
                TryAddSegment(
                    items, line.GetEndPoint(0), line.GetEndPoint(1), layer, linkName, linkId, blockIndex,
                    $"{linkId}:{geomIndex}:ln", "line",
                    "importInstance", null,
                    ref truncated, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY);
                return;
            }

            if (obj is Arc || obj is Curve)
            {
                var c = obj as Curve;
                if (c == null)
                    return;

                AppendCurve(
                    c, null, layer, linkName, linkId, blockIndex, geomIndex,
                    items, ref truncated, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY);
            }
        }

        /// <summary>
        /// Exploded DWG wall faces often live as ModelCurve / DetailCurve, not ImportInstance.
        /// </summary>
        private int AppendModelAndDetailLines(
            Document doc,
            View view,
            List<CadSegmentItem> items,
            ref bool truncated,
            ref bool hasBbox,
            ref double minX,
            ref double minY,
            ref double maxX,
            ref double maxY)
        {
            int added = 0;
            var curves = new FilteredElementCollector(doc, view.Id)
                .OfClass(typeof(CurveElement))
                .WhereElementIsNotElementType()
                .Cast<CurveElement>();

            foreach (var curveEl in curves)
            {
                if (truncated)
                    break;

                if (curveEl is not ModelCurve && curveEl is not DetailCurve)
                    continue;

                Curve geom;
                try
                {
                    geom = curveEl.GeometryCurve;
                }
                catch
                {
                    continue;
                }

                if (geom == null || !geom.IsBound)
                    continue;

                var styleName = string.Empty;
                try
                {
                    styleName = curveEl.LineStyle?.Name ?? string.Empty;
                }
                catch
                {
                    // ignore
                }

                if (!LayerMatches(styleName) && _layerFilters.Count > 0)
                {
                    // Also allow empty style through when no explicit style filter match needed for model lines
                    // unless user asked for a layer — then skip non-matching styles.
                    continue;
                }

                var source = curveEl is DetailCurve ? "detailLine" : "modelLine";
                var id = curveEl.Id.GetValue();
                var before = items.Count;

                if (geom is Line || geom is Arc || geom is Curve)
                {
                    // REV-149: one ArcInfo per curve — every chord shares it, so the client
                    // can group chords back into the original arc.
                    var arcInfo = BuildArcInfo(geom, null, id, 0);

                    IList<XYZ> pts;
                    try
                    {
                        if (geom is Line)
                            pts = new List<XYZ> { geom.GetEndPoint(0), geom.GetEndPoint(1) };
                        else if (arcInfo != null && _arcMode == "single")
                            pts = new List<XYZ> { geom.GetEndPoint(0), geom.GetEndPoint(1) };
                        else
                            pts = geom.Tessellate();
                    }
                    catch
                    {
                        continue;
                    }

                    var curveType = geom is Line ? "line" : (geom is Arc ? "arc" : "curve");
                    for (int i = 0; i < pts.Count - 1; i++)
                    {
                        if (!TryAddSegment(
                                items, pts[i], pts[i + 1],
                                string.IsNullOrEmpty(styleName) ? source : styleName,
                                "modelLines",
                                id,
                                -1,
                                $"{id}:{curveType}{i}",
                                curveType,
                                source,
                                arcInfo,
                                ref truncated, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY))
                            break;
                    }
                }

                added += items.Count - before;
            }

            return added;
        }

        private bool TryAddSegment(
            List<CadSegmentItem> items,
            XYZ start,
            XYZ end,
            string layer,
            string linkName,
            long linkId,
            int blockIndex,
            string cadId,
            string curveType,
            string source,
            ArcInfo arcInfo,
            ref bool truncated,
            ref bool hasBbox,
            ref double minX,
            ref double minY,
            ref double maxX,
            ref double maxY)
        {
            if (items.Count >= _limit)
            {
                truncated = true;
                return false;
            }

            if (start == null || end == null)
                return true;

            var lengthMm = RevitUnitConversion.ToMillimeters(start.DistanceTo(end));
            if (lengthMm < _minLengthMm || lengthMm < 0.1)
                return true;

            var s = ToPointMm(start);
            var e = ToPointMm(end);
            ExpandBbox(s, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY);
            ExpandBbox(e, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY);

            var item = new CadSegmentItem
            {
                StartMm = s,
                EndMm = e,
                Layer = layer,
                CadId = cadId,
                LengthMm = Round1(lengthMm),
                CurveType = curveType,
                CadLinkName = linkName,
                CadLinkElementId = linkId,
                Source = source,
                BlockIndex = blockIndex
            };

            if (arcInfo != null)
            {
                item.ArcId = arcInfo.Id;
                item.ArcCenterMm = arcInfo.CenterMm;
                item.ArcRadiusMm = arcInfo.RadiusMm;
                item.ArcStartAngleDeg = arcInfo.StartAngleDeg;
                item.ArcEndAngleDeg = arcInfo.EndAngleDeg;
            }

            items.Add(item);

            if (blockIndex >= 0 && blockIndex < _blocks.Count)
            {
                var block = _blocks[blockIndex];
                block.SegmentCount++;
                block.BboxMm = ExpandBlockBbox(block.BboxMm, s, e);
                if (string.IsNullOrEmpty(block.Layer) && !string.IsNullOrEmpty(layer))
                    block.Layer = layer;
            }

            return true;
        }

        private static CadBboxMm ExpandBlockBbox(CadBboxMm bbox, CadPointMm a, CadPointMm b)
        {
            bbox ??= new CadBboxMm
            {
                MinX = double.MaxValue,
                MinY = double.MaxValue,
                MaxX = double.MinValue,
                MaxY = double.MinValue
            };

            foreach (var p in new[] { a, b })
            {
                if (p.X < bbox.MinX) bbox.MinX = p.X;
                if (p.Y < bbox.MinY) bbox.MinY = p.Y;
                if (p.X > bbox.MaxX) bbox.MaxX = p.X;
                if (p.Y > bbox.MaxY) bbox.MaxY = p.Y;
            }

            return bbox;
        }

        private View ResolveView(Document doc, UIDocument uiDoc)
        {
            if (_viewId > 0)
            {
                if (doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(_viewId)) is View byId)
                    return byId;
            }

            return uiDoc.ActiveView;
        }

        private static List<ImportInstance> FilterByName(
            Document doc,
            List<ImportInstance> imports,
            string nameFilter)
        {
            if (string.IsNullOrWhiteSpace(nameFilter))
                return imports;

            return imports
                .Where(i =>
                {
                    var name = GetCadName(doc, i);
                    return name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                })
                .ToList();
        }

        private bool LayerMatches(string layer)
        {
            if (_layerFilters.Count == 0)
                return true;

            if (string.IsNullOrEmpty(layer))
                return false;

            return _layerFilters.Any(f =>
                layer.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(layer, f, StringComparison.OrdinalIgnoreCase));
        }

        private static string ResolveLayer(Document doc, GeometryObject obj)
        {
            try
            {
                if (obj.GraphicsStyleId == ElementId.InvalidElementId)
                    return string.Empty;

                if (doc.GetElement(obj.GraphicsStyleId) is GraphicsStyle style)
                    return style.GraphicsStyleCategory?.Name ?? string.Empty;
            }
            catch
            {
                // Some imported styles throw; treat as empty layer.
            }

            return string.Empty;
        }

        private static bool IsLayerVisible(Document doc, View view, GeometryObject obj)
        {
            try
            {
                if (obj.GraphicsStyleId == ElementId.InvalidElementId)
                    return true;

                if (doc.GetElement(obj.GraphicsStyleId) is not GraphicsStyle style)
                    return true;

                var cat = style.GraphicsStyleCategory;
                if (cat == null)
                    return true;

                return !view.GetCategoryHidden(cat.Id);
            }
            catch
            {
                return true;
            }
        }

        private static string GetCadName(Document doc, ImportInstance import)
        {
            var type = doc.GetElement(import.GetTypeId());
            if (type != null && !string.IsNullOrWhiteSpace(type.Name))
                return type.Name;

            var symbol = import.get_Parameter(BuiltInParameter.IMPORT_SYMBOL_NAME)?.AsString();
            if (!string.IsNullOrWhiteSpace(symbol))
                return symbol;

            if (import.Category != null && !string.IsNullOrWhiteSpace(import.Category.Name))
                return import.Category.Name;

            return $"ImportInstance {import.Id.GetValue()}";
        }

        private static List<CadLinkInfo> BuildLinkInfos(Document doc, IEnumerable<ImportInstance> imports)
        {
            return imports.Select(i => new CadLinkInfo
            {
                ElementId = i.Id.GetValue(),
                Name = GetCadName(doc, i),
                IsLinked = i.IsLinked,
                SegmentCount = 0
            }).ToList();
        }

        private static CadPointMm ToPointMm(XYZ p) => new CadPointMm
        {
            X = Round1(RevitUnitConversion.ToMillimeters(p.X)),
            Y = Round1(RevitUnitConversion.ToMillimeters(p.Y)),
            Z = Round1(RevitUnitConversion.ToMillimeters(p.Z))
        };

        private static void ExpandBbox(
            CadPointMm p,
            ref bool hasBbox,
            ref double minX,
            ref double minY,
            ref double maxX,
            ref double maxY)
        {
            if (!hasBbox)
            {
                minX = maxX = p.X;
                minY = maxY = p.Y;
                hasBbox = true;
                return;
            }

            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }

        private static double Round1(double v) => Math.Round(v, 1);

        private static GetCadLinkGeometryResult Fail(
            string message,
            View view = null,
            List<CadLinkInfo> available = null)
        {
            return new GetCadLinkGeometryResult
            {
                Ok = false,
                Summary = message,
                Message = message,
                Count = 0,
                Items = new List<CadSegmentItem>(),
                ViewId = view?.Id.GetValue(),
                ViewName = view?.Name,
                AvailableLinks = available,
                ImportUnits = "mm"
            };
        }

        public string GetName() => "Get CAD Link Geometry";
    }
}
