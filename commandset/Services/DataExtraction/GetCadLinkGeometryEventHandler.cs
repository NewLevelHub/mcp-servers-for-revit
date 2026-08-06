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

        public GetCadLinkGeometryResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        public void SetParameters(
            string cadLinkName = "",
            IEnumerable<string> layerFilters = null,
            long viewId = 0,
            double minLengthMm = 0,
            int limit = 5000,
            bool includeHiddenLayers = false,
            bool includeModelLines = false)
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

                    foreach (GeometryObject top in geometry)
                    {
                        if (truncated)
                            break;

                        AppendGeometryObject(
                            doc, view, top, linkName, linkId, ref geomIndex,
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

                ResultInfo = new GetCadLinkGeometryResult
                {
                    Ok = true,
                    Summary = selected.Count <= 1
                        ? $"{(primaryName != null ? $"DWG «{primaryName}»" : "Линии")}: {items.Count} сегмент(ов)" +
                          (string.IsNullOrEmpty(layerHint) ? "" : $", слои {layerHint}") +
                          sourceHint +
                          (truncated ? " (урезано)" : "")
                        : $"CAD ×{selected.Count}: {items.Count} сегмент(ов)" + sourceHint + (truncated ? " (урезано)" : ""),
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
                    LayerSummary = layerSummary
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
                bool instanceHasCurves = GeometryElementHasDrawableCurves(instanceGeom);

                if (instanceHasCurves)
                {
                    foreach (GeometryObject child in instanceGeom)
                    {
                        if (truncated)
                            break;
                        AppendGeometryObject(
                            doc, view, child, linkName, linkId, ref geomIndex,
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
                                doc, view, child, transform, linkName, linkId, ref geomIndex,
                                items, ref truncated, ref hasBbox,
                                ref minX, ref minY, ref maxX, ref maxY);
                        }
                    }
                }

                return;
            }

            AppendObject(
                doc, view, obj, linkName, linkId, geomIndex++,
                items, ref truncated, ref hasBbox,
                ref minX, ref minY, ref maxX, ref maxY);
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
                    foreach (GeometryObject child in instanceGeom)
                    {
                        if (truncated)
                            break;
                        AppendTransformedGeometryObject(
                            doc, view, child, combined, linkName, linkId, ref geomIndex,
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
                            layer, linkName, linkId,
                            $"{linkId}:{geomIndex}:pl{i}", "polylineSegment",
                            "importInstance",
                            ref truncated, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY))
                        return;
                }

                return;
            }

            if (obj is Line line)
            {
                TryAddSegment(
                    items, transform.OfPoint(line.GetEndPoint(0)), transform.OfPoint(line.GetEndPoint(1)),
                    layer, linkName, linkId,
                    $"{linkId}:{geomIndex}:ln", "line",
                    "importInstance",
                    ref truncated, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY);
                return;
            }

            if (obj is Arc || obj is Curve)
            {
                var c = obj as Curve;
                if (c == null)
                    return;

                IList<XYZ> pts;
                try
                {
                    pts = c.Tessellate();
                }
                catch
                {
                    pts = new List<XYZ> { c.GetEndPoint(0), c.GetEndPoint(1) };
                }

                var curveType = obj is Arc ? "arc" : "curve";
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    if (!TryAddSegment(
                            items, transform.OfPoint(pts[i]), transform.OfPoint(pts[i + 1]),
                            layer, linkName, linkId,
                            $"{linkId}:{geomIndex}:{curveType}{i}", curveType,
                            "importInstance",
                            ref truncated, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY))
                        return;
                }
            }
        }

        private void AppendObject(
            Document doc,
            View view,
            GeometryObject obj,
            string linkName,
            long linkId,
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
                            items, coords[i], coords[i + 1], layer, linkName, linkId,
                            $"{linkId}:{geomIndex}:pl{i}", "polylineSegment",
                            "importInstance",
                            ref truncated, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY))
                        return;
                }

                return;
            }

            if (obj is Line line)
            {
                TryAddSegment(
                    items, line.GetEndPoint(0), line.GetEndPoint(1), layer, linkName, linkId,
                    $"{linkId}:{geomIndex}:ln", "line",
                    "importInstance",
                    ref truncated, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY);
                return;
            }

            if (obj is Arc || obj is Curve)
            {
                var c = obj as Curve;
                if (c == null)
                    return;

                IList<XYZ> pts;
                try
                {
                    pts = c.Tessellate();
                }
                catch
                {
                    pts = new List<XYZ> { c.GetEndPoint(0), c.GetEndPoint(1) };
                }

                var curveType = obj is Arc ? "arc" : "curve";
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    if (!TryAddSegment(
                            items, pts[i], pts[i + 1], layer, linkName, linkId,
                            $"{linkId}:{geomIndex}:{curveType}{i}", curveType,
                            "importInstance",
                            ref truncated, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY))
                        return;
                }
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
                    IList<XYZ> pts;
                    try
                    {
                        if (geom is Line)
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
                                $"{id}:{curveType}{i}",
                                curveType,
                                source,
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
            string cadId,
            string curveType,
            string source,
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

            items.Add(new CadSegmentItem
            {
                StartMm = s,
                EndMm = e,
                Layer = layer,
                CadId = cadId,
                LengthMm = Round1(lengthMm),
                CurveType = curveType,
                CadLinkName = linkName,
                CadLinkElementId = linkId,
                Source = source
            });
            return true;
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
