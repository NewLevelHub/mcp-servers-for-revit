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

        public GetCadLinkGeometryResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        public void SetParameters(
            string cadLinkName = "",
            IEnumerable<string> layerFilters = null,
            long viewId = 0,
            double minLengthMm = 0,
            int limit = 5000)
        {
            _cadLinkName = cadLinkName ?? string.Empty;
            _layerFilters = (layerFilters ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList();
            _viewId = viewId;
            _minLengthMm = Math.Max(0, minLengthMm);
            _limit = limit > 0 ? limit : 5000;
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

                if (importsOnView.Count == 0)
                {
                    var hint = allInDoc.Count == 0
                        ? "На виде нет CAD/DWG. Привяжите DWG к уровню (Вставка → Связь CAD) и откройте план этажа."
                        : $"На виде «{view.Name}» нет видимых CAD-подложек (в проекте есть {allInDoc.Count}). " +
                          "Покажите связь на этом плане или привяжите DWG к уровню вида.";

                    ResultInfo = Fail(hint, view, BuildLinkInfos(doc, allInDoc));
                    return;
                }

                var selected = FilterByName(doc, importsOnView, _cadLinkName);
                if (selected.Count == 0)
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
                    IncludeNonVisibleObjects = true
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

                        if (top is GeometryInstance gi)
                        {
                            var instanceGeom = gi.GetInstanceGeometry();
                            if (instanceGeom == null)
                                continue;

                            foreach (GeometryObject obj in instanceGeom)
                            {
                                if (truncated)
                                    break;

                                AppendObject(
                                    doc, view, obj, linkName, linkId, geomIndex++,
                                    items, ref truncated, ref hasBbox,
                                    ref minX, ref minY, ref maxX, ref maxY);
                            }
                        }
                        else
                        {
                            AppendObject(
                                doc, view, top, linkName, linkId, geomIndex++,
                                items, ref truncated, ref hasBbox,
                                ref minX, ref minY, ref maxX, ref maxY);
                        }
                    }

                    linkInfos.Add(new CadLinkInfo
                    {
                        ElementId = linkId,
                        Name = linkName,
                        IsLinked = import.IsLinked,
                        SegmentCount = items.Count - before
                    });
                }

                if (items.Count == 0)
                {
                    ResultInfo = Fail(
                        "CAD найден, но линий/дуг/полилиний нет (или все отфильтрованы по слою/длине). " +
                        "Проверьте видимость слоёв DWG на виде.",
                        view,
                        BuildLinkInfos(doc, selected));
                    return;
                }

                var primary = selected[0];
                var primaryName = GetCadName(doc, primary);
                var layers = items.Select(i => i.Layer).Where(l => !string.IsNullOrEmpty(l)).Distinct().Take(8);
                var layerHint = string.Join("/", layers);

                ResultInfo = new GetCadLinkGeometryResult
                {
                    Ok = true,
                    Summary = selected.Count == 1
                        ? $"DWG «{primaryName}»: {items.Count} сегмент(ов)" +
                          (string.IsNullOrEmpty(layerHint) ? "" : $", слои {layerHint}") +
                          (truncated ? " (урезано)" : "")
                        : $"CAD ×{selected.Count}: {items.Count} сегмент(ов)" + (truncated ? " (урезано)" : ""),
                    Count = items.Count,
                    Items = items,
                    BboxMm = hasBbox
                        ? new CadBboxMm { MinX = Round1(minX), MinY = Round1(minY), MaxX = Round1(maxX), MaxY = Round1(maxY) }
                        : null,
                    CadLinkName = selected.Count == 1 ? primaryName : null,
                    CadLinkElementId = selected.Count == 1 ? primary.Id.GetValue() : null,
                    ImportUnits = "mm",
                    ViewId = view.Id.GetValue(),
                    ViewName = view.Name,
                    AvailableLinks = linkInfos,
                    Truncated = truncated,
                    Message = truncated
                        ? $"Показаны первые {_limit} сегментов. Уточните layerFilter или увеличьте limit."
                        : null
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

            if (!IsLayerVisible(doc, view, obj))
                return;

            if (obj is PolyLine poly)
            {
                var coords = poly.GetCoordinates();
                for (int i = 0; i < coords.Count - 1; i++)
                {
                    if (!TryAddSegment(
                            items, coords[i], coords[i + 1], layer, linkName, linkId,
                            $"{linkId}:{geomIndex}:pl{i}", "polylineSegment",
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
                            ref truncated, ref hasBbox, ref minX, ref minY, ref maxX, ref maxY))
                        return;
                }
            }
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
                CadLinkElementId = linkId
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
