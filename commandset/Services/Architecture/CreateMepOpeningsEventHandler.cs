using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Architecture;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Architecture
{
    /// <summary>
    /// Turns the engineering runs of a link crossing our structure into a задание на
    /// отверстия (REV-168): where a hole is needed, how big, and — once confirmed — the
    /// holes themselves.
    /// </summary>
    /// <remarks>
    /// Built on the crossing geometry of REV-167, with one difference that decides the
    /// whole design: a clash report only has to say «здесь бьётся», while a задание has
    /// to say «вот такой прямоугольник». So the overlap body is not just measured — it
    /// is projected into the plane of the wall (or the plan of the slab) and the
    /// rectangle read off there. An angled pipe therefore gets the wider hole it
    /// actually needs, without anybody computing an ellipse.
    ///
    /// **Preview first, always.** With <c>apply=false</c> — the default — no transaction
    /// is opened at all. Cutting holes in someone's model on the strength of a guessed
    /// intent is the one mistake this tool must never make, so the plan is returned and
    /// the caller has to come back and say yes.
    ///
    /// **A re-run must not double up.** Existing openings are read before anything is
    /// created and matched by host and position, so the second run reports «exists»
    /// rather than cutting the same hole again.
    /// </remarks>
    public class CreateMepOpeningsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private string _linkNameFilter = string.Empty;
        private string _levelName = string.Empty;
        private List<string> _mepCategoryNames = new List<string>();
        private double _clearanceMm = MepOpeningRules.DefaultClearanceMm;
        private double _mergeGapMm = MepOpeningRules.DefaultMergeGapMm;
        private double _sizeStepMm = MepOpeningRules.DefaultSizeStepMm;
        private bool _apply;
        private long _openingTypeId;
        private int _maxOpenings = 200;
        private int _timeBudgetSeconds = 90;

        private const double MinSolidVolume = 1e-6;

        /// <summary>How close an existing opening has to be to count as the same one.</summary>
        private const double ExistingMatchMm = 150.0;

        /// <summary>Марка prefix — also how a re-run recognises the openings it made.</summary>
        private const string MarkPrefix = "ОТВ";

        public CreateMepOpeningsResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        public void SetParameters(
            string linkNameFilter = "",
            string levelName = "",
            IEnumerable<string> mepCategories = null,
            double clearanceMm = MepOpeningRules.DefaultClearanceMm,
            double mergeGapMm = MepOpeningRules.DefaultMergeGapMm,
            double sizeStepMm = MepOpeningRules.DefaultSizeStepMm,
            bool apply = false,
            long openingTypeId = 0,
            int maxOpenings = 200,
            int timeBudgetSeconds = 90)
        {
            _linkNameFilter = linkNameFilter ?? string.Empty;
            _levelName = levelName ?? string.Empty;
            _mepCategoryNames = (mepCategories ?? Enumerable.Empty<string>()).ToList();
            _clearanceMm = Math.Max(0, clearanceMm);
            _mergeGapMm = Math.Max(0, mergeGapMm);
            _sizeStepMm = Math.Max(0, sizeStepMm);
            _apply = apply;
            _openingTypeId = openingTypeId;
            _maxOpenings = Math.Max(1, Math.Min(2000, maxOpenings));
            _timeBudgetSeconds = Math.Max(5, Math.Min(150, timeBudgetSeconds));
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            var total = Stopwatch.StartNew();

            try
            {
                var doc = app.ActiveUIDocument.Document;
                var result = new CreateMepOpeningsResult
                {
                    Success = true,
                    HostModel = doc.Title ?? string.Empty,
                    Applied = false,
                    ClearanceMm = _clearanceMm,
                    MergeGapMm = _mergeGapMm,
                    SizeStepMm = _sizeStepMm
                };

                var links = CollectLinks(doc, result);
                var mepCategories = ClashCategories.Resolve(
                    links.FirstOrDefault()?.LinkDocument, _mepCategoryNames, DefaultMepCategories, out _);

                var crossings = Measure(doc, result, links, mepCategories, total);
                result.RawCrossings = crossings.Count;

                // Two foldings, in this order. First пачки труб on one element, then the
                // layers of one wall — the second needs the first to have settled, or a
                // bundle in the concrete would not recognise the same bundle in the
                // insulation as its own stack.
                var openings = MepOpeningRules.FoldLayers(Fold(crossings));
                Renumber(openings);
                result.Openings = openings;
                result.TotalOpenings = openings.Count;

                MarkExisting(doc, openings);
                result.SkippedExistingCount = openings.Count(item => item.Status == "exists");

                if (_apply)
                {
                    Apply(doc, result, openings);
                    result.Applied = true;
                }

                total.Stop();
                result.ElapsedMs = total.ElapsedMilliseconds;
                result.Message = BuildMessage(result);
                ResultInfo = result;
            }
            catch (Exception ex)
            {
                total.Stop();
                ResultInfo = new CreateMepOpeningsResult
                {
                    Success = false,
                    ElapsedMs = total.ElapsedMilliseconds,
                    Message = $"Задание на отверстия не собрано: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        // --- what we look for ----------------------------------------------------

        /// <summary>
        /// The runs that need a hole. Fittings and equipment are deliberately out: a
        /// задание is cut for a pipe passing through, and a bend sitting inside a wall is
        /// a coordination problem to argue about, not a hole to order.
        /// </summary>
        private static readonly IReadOnlyList<BuiltInCategory> DefaultMepCategories = new[]
        {
            BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_FlexPipeCurves,
            BuiltInCategory.OST_DuctCurves,
            BuiltInCategory.OST_FlexDuctCurves,
            BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_Conduit,
        };

        private sealed class LinkTarget
        {
            public Document LinkDocument;
            public Transform ToHost;
            public Transform ToLink;
            public MepOpeningLinkInfo Info;
        }

        /// <summary>
        /// Axes of the plane an opening is drawn in: U along the wall, V up it, W through
        /// it. For a slab, U/V are plan X/Y and W is the thickness.
        /// </summary>
        private readonly struct HostFrame
        {
            public HostFrame(XYZ u, XYZ v, XYZ w)
            {
                U = u;
                V = v;
                W = w;
            }

            public XYZ U { get; }
            public XYZ V { get; }
            public XYZ W { get; }

            public XYZ ToWorld(double uFt, double vFt, double wFt) =>
                U.Multiply(uFt).Add(V.Multiply(vFt)).Add(W.Multiply(wFt));
        }

        private sealed class Crossing
        {
            public Element Host;
            public string HostKind;
            public HostFrame Frame;
            public LinkTarget Link;
            public Element Mep;
            public MepOpeningRules.OpeningRect Rect;
            public double CentreWMm;

            /// <summary>Thickness of the host at this spot, mm — read off its own solid.</summary>
            public double HostThicknessMm;

            /// <summary>How far the run got across the host, mm — the through-or-along test.</summary>
            public double ThroughMm;
        }

        // --- links ---------------------------------------------------------------

        /// <summary>
        /// Reads the links the same way <c>CheckLinkClashesEventHandler</c> does, and for
        /// the same reason: an unloaded ИОС link quietly skipped would produce an empty
        /// задание, which reads as «отверстий не нужно».
        /// </summary>
        private List<LinkTarget> CollectLinks(Document doc, CreateMepOpeningsResult result)
        {
            var targets = new List<LinkTarget>();

            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .WhereElementIsNotElementType()
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var instance in instances)
            {
                var info = new MepOpeningLinkInfo { LinkInstanceId = instance.Id.GetValue() };

                try
                {
                    var linkType = doc.GetElement(instance.GetTypeId()) as RevitLinkType;
                    info.LinkName = !string.IsNullOrWhiteSpace(linkType?.Name) ? linkType.Name : instance.Name;

                    if (!string.IsNullOrWhiteSpace(_linkNameFilter) &&
                        info.LinkName.IndexOf(_linkNameFilter, StringComparison.CurrentCultureIgnoreCase) < 0)
                    {
                        continue;
                    }

                    var discipline = LinkDisciplineClassifier.Classify(info.LinkName);
                    info.Section = discipline.IsKnown ? discipline.Display : null;

                    var linkDoc = instance.GetLinkDocument();
                    if (linkDoc == null)
                    {
                        info.Scanned = false;
                        info.Note = "Связь недоступна (выгружена или не найдена) — отверстия по ней не посчитаны.";
                        result.Links.Add(info);
                        continue;
                    }

                    info.Scanned = true;
                    result.Links.Add(info);

                    var toHost = instance.GetTotalTransform();
                    targets.Add(new LinkTarget
                    {
                        LinkDocument = linkDoc,
                        ToHost = toHost,
                        ToLink = toHost.Inverse,
                        Info = info
                    });
                }
                catch (Exception ex)
                {
                    info.Scanned = false;
                    info.Note = $"Связь не прочитана: {ex.Message}";
                    result.Links.Add(info);
                }
            }

            return targets;
        }

        // --- measuring -----------------------------------------------------------

        private List<Crossing> Measure(
            Document doc,
            CreateMepOpeningsResult result,
            List<LinkTarget> links,
            List<BuiltInCategory> mepCategories,
            Stopwatch total)
        {
            var crossings = new List<Crossing>();
            if (links.Count == 0 || mepCategories.Count == 0)
                return crossings;

            var levelId = FindLevelId(doc, _levelName);
            var levelScoped = levelId != null && levelId != ElementId.InvalidElementId;
            result.Scope = levelScoped
                ? $"уровень «{_levelName}»"
                : string.IsNullOrWhiteSpace(_levelName)
                    ? "вся модель"
                    : $"вся модель (уровень «{_levelName}» не найден)";

            var mepFilter = new ElementMulticategoryFilter(mepCategories);
            var budget = TimeSpan.FromSeconds(_timeBudgetSeconds);

            foreach (var host in CollectHosts(doc, levelScoped ? levelId : null))
            {
                if (total.Elapsed > budget || crossings.Count >= _maxOpenings * 4)
                {
                    result.Truncated = true;
                    break;
                }

                var frame = BuildFrame(host, out var hostKind);
                if (frame == null)
                    continue;

                var hostSolids = ReadSolids(host);
                if (hostSolids.Count == 0)
                    continue;

                foreach (var link in links)
                {
                    foreach (var hostSolid in hostSolids)
                    {
                        // The thickness at this spot, read off the element itself rather
                        // than from a type parameter: a multi-layer wall is several
                        // elements here, and each layer has its own.
                        var thicknessMm =
                            TryMeasure(hostSolid, frame.Value, out _, out var hostWMin, out var hostWMax)
                                ? hostWMax - hostWMin
                                : 0;

                        var inLink = SolidUtils.CreateTransformed(hostSolid, link.ToLink);
                        var outline = BuildOutline(inLink);
                        if (outline == null)
                            continue;

                        var collector = new FilteredElementCollector(link.LinkDocument)
                            .WhereElementIsNotElementType()
                            .WherePasses(mepFilter)
                            .WherePasses(new BoundingBoxIntersectsFilter(outline))
                            .WherePasses(new ElementIntersectsSolidFilter(inLink));

                        foreach (var mep in collector)
                        {
                            var crossing = MeasureCrossing(host, hostKind, frame.Value, link, mep, inLink);
                            if (crossing == null)
                                continue;

                            crossing.HostThicknessMm = Math.Round(thicknessMm, 1);

                            // A run lying along the inside of a wall needs the смежник to
                            // move it, not a 6-metre hole cut through the building.
                            if (!MepOpeningRules.PassesThrough(crossing.ThroughMm, thicknessMm))
                            {
                                result.SkippedNotThrough++;
                                continue;
                            }

                            crossings.Add(crossing);
                        }
                    }
                }
            }

            return crossings;
        }

        /// <summary>
        /// The footprint one engineering run leaves on one of our elements, read in the
        /// plane the opening will be drawn in. Null when the two turn out not to share
        /// any measurable volume after all.
        /// </summary>
        private Crossing MeasureCrossing(
            Element host,
            string hostKind,
            HostFrame frame,
            LinkTarget link,
            Element mep,
            Solid hostSolidInLink)
        {
            MepOpeningRules.OpeningRect? rect = null;
            double wMin = 0, wMax = 0;

            foreach (var mepSolid in ReadSolids(mep))
            {
                try
                {
                    var overlap = BooleanOperationsUtils.ExecuteBooleanOperation(
                        hostSolidInLink, mepSolid, BooleanOperationsType.Intersect);

                    if (overlap == null || overlap.Volume <= MinSolidVolume)
                        continue;

                    // Measured in OUR coordinates: the opening is drawn in our model, and
                    // the link may sit rotated relative to it.
                    var inHost = SolidUtils.CreateTransformed(overlap, link.ToHost);
                    if (!TryMeasure(inHost, frame, out var measured, out var thisWMin, out var thisWMax))
                        continue;

                    if (rect == null)
                    {
                        rect = measured;
                        wMin = thisWMin;
                        wMax = thisWMax;
                    }
                    else
                    {
                        rect = rect.Value.Union(measured);
                        wMin = Math.Min(wMin, thisWMin);
                        wMax = Math.Max(wMax, thisWMax);
                    }
                }
                catch
                {
                    // Boolean operations fail on the near-degenerate geometry that is
                    // ordinary in a subcontractor's file. One unmeasurable run must not
                    // cost the whole задание.
                }
            }

            if (rect == null)
                return null;

            return new Crossing
            {
                Host = host,
                HostKind = hostKind,
                Frame = frame,
                Link = link,
                Mep = mep,
                Rect = rect.Value,
                CentreWMm = (wMin + wMax) / 2.0,
                ThroughMm = wMax - wMin
            };
        }

        /// <summary>
        /// Extents of a solid along the axes of the host, millimetres. The vertices come
        /// off the edges rather than from GetBoundingBox, which reports in a frame of its
        /// own choosing and would silently measure the wrong rectangle on a rotated wall.
        /// </summary>
        private static bool TryMeasure(
            Solid solid,
            HostFrame frame,
            out MepOpeningRules.OpeningRect rect,
            out double wMinMm,
            out double wMaxMm)
        {
            rect = default;
            wMinMm = 0;
            wMaxMm = 0;

            double minU = double.MaxValue, maxU = double.MinValue;
            double minV = double.MaxValue, maxV = double.MinValue;
            double minW = double.MaxValue, maxW = double.MinValue;
            var seen = false;

            foreach (Edge edge in solid.Edges)
            {
                IList<XYZ> points;
                try
                {
                    points = edge.Tessellate();
                }
                catch
                {
                    continue;
                }

                foreach (var point in points ?? new List<XYZ>())
                {
                    if (point == null)
                        continue;

                    seen = true;
                    var u = point.DotProduct(frame.U);
                    var v = point.DotProduct(frame.V);
                    var w = point.DotProduct(frame.W);

                    minU = Math.Min(minU, u);
                    maxU = Math.Max(maxU, u);
                    minV = Math.Min(minV, v);
                    maxV = Math.Max(maxV, v);
                    minW = Math.Min(minW, w);
                    maxW = Math.Max(maxW, w);
                }
            }

            if (!seen)
                return false;

            rect = new MepOpeningRules.OpeningRect(
                RevitUnitConversion.ToMillimeters(minU),
                RevitUnitConversion.ToMillimeters(maxU),
                RevitUnitConversion.ToMillimeters(minV),
                RevitUnitConversion.ToMillimeters(maxV));

            wMinMm = RevitUnitConversion.ToMillimeters(minW);
            wMaxMm = RevitUnitConversion.ToMillimeters(maxW);
            return true;
        }

        /// <summary>
        /// The plane an opening in this element is drawn in, or null when the element is
        /// not something we know how to cut.
        /// </summary>
        private static HostFrame? BuildFrame(Element host, out string hostKind)
        {
            hostKind = null;

            if (host is Wall wall)
            {
                hostKind = "wall";

                var direction = (wall.Location as LocationCurve)?.Curve is Line line
                    ? line.Direction
                    : null;

                if (direction == null || direction.GetLength() < 1e-9)
                {
                    // A curved wall has no single plane to draw a rectangle in. Saying so
                    // is better than cutting a hole in the wrong place.
                    return null;
                }

                var u = new XYZ(direction.X, direction.Y, 0);
                if (u.GetLength() < 1e-9)
                    return null;

                u = u.Normalize();
                var v = XYZ.BasisZ;
                return new HostFrame(u, v, u.CrossProduct(v).Normalize());
            }

            if (host is Floor)
            {
                hostKind = "floor";
                // A slab is cut in plan, so the rectangle is read along the project axes.
                // A diagonal duct therefore gets an axis-aligned hole a little larger
                // than strictly needed — buildable, and honest about it in the sizes.
                return new HostFrame(XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ);
            }

            return null;
        }

        private static List<Element> CollectHosts(Document doc, ElementId levelId)
        {
            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent()
                .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory>
                {
                    BuiltInCategory.OST_Walls,
                    BuiltInCategory.OST_Floors,
                }));

            if (levelId != null && levelId != ElementId.InvalidElementId)
                collector = collector.WherePasses(new ElementLevelFilter(levelId));

            return collector.ToElements().ToList();
        }

        // --- folding into openings ----------------------------------------------

        /// <summary>
        /// One hole per bundle. Five pipes side by side get one opening rather than five
        /// holes with fins of masonry between them that nobody would build.
        /// </summary>
        private List<MepOpeningPlanItem> Fold(List<Crossing> crossings)
        {
            var openings = new List<MepOpeningPlanItem>();

            foreach (var group in crossings.GroupBy(crossing => crossing.Host.Id.GetValue()))
            {
                var clustered = MepOpeningRules.Cluster(
                    group.Select(crossing => new List<Crossing> { crossing }).ToList(),
                    bundle => bundle
                        .Select(crossing => crossing.Rect)
                        .Aggregate((a, b) => a.Union(b)),
                    (a, b, _) => a.Concat(b).ToList(),
                    _mergeGapMm);

                foreach (var bundle in clustered)
                {
                    var item = Describe(bundle);
                    if (item == null)
                        continue;

                    // Marks are handed out after the layers are folded — numbering here
                    // would leave the задание with gaps where a layer was absorbed.
                    openings.Add(item);
                    if (openings.Count >= _maxOpenings)
                        return openings;
                }
            }

            return openings;
        }

        /// <summary>
        /// Marks, once the list is final. Numbered per level and in the order the openings
        /// come out, so «ОТВ-2эт-03» means the same thing on the plan and in the ведомость.
        /// </summary>
        private static void Renumber(List<MepOpeningPlanItem> openings)
        {
            var perLevelIndex = new Dictionary<string, int>(StringComparer.CurrentCulture);

            foreach (var item in openings)
            {
                var levelKey = item.HostLevel ?? string.Empty;
                perLevelIndex.TryGetValue(levelKey, out var index);
                perLevelIndex[levelKey] = ++index;
                item.Mark = MepOpeningRules.BuildMark(item.HostLevel, index);
            }
        }

        private MepOpeningPlanItem Describe(List<Crossing> bundle)
        {
            var first = bundle.FirstOrDefault();
            if (first == null)
                return null;

            var doc = first.Host.Document;
            var rect = bundle.Select(crossing => crossing.Rect).Aggregate((a, b) => a.Union(b));
            var size = MepOpeningRules.SizeForDrawing(rect, _clearanceMm, _sizeStepMm);
            var centreW = bundle.Average(crossing => crossing.CentreWMm);

            var centre = first.Frame.ToWorld(
                RevitUnitConversion.FromMillimeters(rect.CentreU),
                RevitUnitConversion.FromMillimeters(rect.CentreV),
                RevitUnitConversion.FromMillimeters(centreW));

            var levelName = ReadLevelName(doc, first.Host, out var levelElevationMm);

            var item = new MepOpeningPlanItem
            {
                HostElementId = first.Host.Id.GetValue(),
                HostKind = first.HostKind,
                HostCategory = first.Host.Category?.Name ?? string.Empty,
                HostType = ReadTypeName(doc, first.Host),
                HostLevel = levelName,
                HostThicknessMm = bundle.Max(crossing => crossing.HostThicknessMm),
                LinkName = first.Link.Info.LinkName,
                LinkSection = first.Link.Info.Section,
                MepElementIds = bundle.Select(crossing => crossing.Mep.Id.GetValue()).Distinct().ToList(),
                MepDescription = DescribeMep(bundle),
                WidthMm = size.WidthMm,
                HeightMm = size.HeightMm,
                MeasuredWidthMm = Math.Round(rect.WidthMm, 1),
                MeasuredHeightMm = Math.Round(rect.HeightMm, 1),
                CentreMm = ToMillimetres(centre),
            };

            if (first.HostKind == "wall")
            {
                // Отметка низа: what a монтажник measures up from the floor.
                item.BottomAboveLevelMm =
                    Math.Round(rect.CentreV - size.HeightMm / 2.0 - levelElevationMm, 1);
            }

            foreach (var crossing in bundle)
                crossing.Link.Info.OpeningCount++;

            return item;
        }

        /// <summary>«Труба ⌀110 ×2, Воздуховод 400×200» — what goes through the hole.</summary>
        private static string DescribeMep(List<Crossing> bundle)
        {
            var counts = new Dictionary<string, int>(StringComparer.CurrentCulture);

            foreach (var crossing in bundle)
            {
                var text = DescribeOne(crossing.Mep);
                counts.TryGetValue(text, out var current);
                counts[text] = current + 1;
            }

            return string.Join(", ", counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.CurrentCulture)
                .Select(pair => pair.Value > 1 ? $"{pair.Key} ×{pair.Value}" : pair.Key));
        }

        /// <summary>
        /// Size read off the built-in MEP parameters rather than by casting to Pipe or
        /// Duct — that would drag the Plumbing and Mechanical namespaces in for a label.
        /// </summary>
        private static string DescribeOne(Element mep)
        {
            var category = mep?.Category?.Name ?? "Элемент";

            var diameter = ReadLengthMm(mep, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
            if (diameter > 0)
                return $"{category} ⌀{Math.Round(diameter)}";

            var width = ReadLengthMm(mep, BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
            var height = ReadLengthMm(mep, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
            if (width > 0 && height > 0)
                return $"{category} {Math.Round(width)}×{Math.Round(height)}";

            return category;
        }

        private static double ReadLengthMm(Element element, BuiltInParameter bip)
        {
            try
            {
                var param = element?.get_Parameter(bip);
                if (param == null || param.StorageType != StorageType.Double)
                    return 0;

                return RevitUnitConversion.ToMillimeters(param.AsDouble());
            }
            catch
            {
                return 0;
            }
        }

        // --- what is already there ----------------------------------------------

        /// <summary>
        /// Marks the openings that already exist, so a second run reports them instead of
        /// cutting the same hole twice. Read-only: it runs before any transaction.
        /// </summary>
        private static void MarkExisting(Document doc, List<MepOpeningPlanItem> openings)
        {
            var existing = new List<(long HostId, XYZ Centre)>();

            try
            {
                foreach (var opening in new FilteredElementCollector(doc)
                             .OfClass(typeof(Opening))
                             .Cast<Opening>())
                {
                    var box = opening.get_BoundingBox(null);
                    if (box == null)
                        continue;

                    existing.Add((opening.Host?.Id.GetValue() ?? 0, (box.Min + box.Max) / 2.0));
                }

                foreach (var instance in new FilteredElementCollector(doc)
                             .OfClass(typeof(FamilyInstance))
                             .Cast<FamilyInstance>())
                {
                    if (instance.Host == null || !(instance.Location is LocationPoint location))
                        continue;

                    // Only our own openings count. Matching any hosted instance would let
                    // a door standing near a planned hole cancel it — and the задание
                    // would quietly come back one opening short.
                    var mark = instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                    if (string.IsNullOrEmpty(mark) || !mark.StartsWith(MarkPrefix, StringComparison.CurrentCultureIgnoreCase))
                        continue;

                    existing.Add((instance.Host.Id.GetValue(), location.Point));
                }
            }
            catch
            {
                // Without the reading a re-run may duplicate, which is bad but visible.
                // Failing the whole задание over it would be worse.
                return;
            }

            var radiusFt = RevitUnitConversion.FromMillimeters(ExistingMatchMm);

            foreach (var item in openings)
            {
                var centre = ToFeet(item.CentreMm);

                var match = existing.FirstOrDefault(candidate =>
                    candidate.HostId == item.HostElementId &&
                    candidate.Centre != null &&
                    candidate.Centre.DistanceTo(centre) <= radiusFt);

                if (match.Centre == null)
                    continue;

                item.Status = "exists";
                item.Note = "Проём здесь уже есть — повторно не создаётся.";
            }
        }

        // --- creating ------------------------------------------------------------

        private void Apply(Document doc, CreateMepOpeningsResult result, List<MepOpeningPlanItem> openings)
        {
            var pending = openings.Where(item => item.Status == "planned").ToList();
            if (pending.Count == 0)
                return;

            var source = ResolveOpeningSymbol(doc, _openingTypeId);

            using (var tx = new Transaction(doc, "MCP Задание на отверстия"))
            {
                tx.Start();

                foreach (var item in pending)
                {
                    try
                    {
                        var created = item.HostKind == "floor"
                            ? CreateFloorOpening(doc, item)
                            : CreateWallOpening(doc, item, source);

                        if (created == null)
                        {
                            item.Status = "failed";
                            item.Note ??= "Проём создать не удалось.";
                            result.FailedCount++;
                            continue;
                        }

                        item.Status = "created";
                        item.OpeningElementId = created.Id.GetValue();
                        result.CreatedCount++;

                        // The hole is one hole, but the wall is a stack of elements and
                        // Revit cuts each of them separately. Stopping at the structural
                        // layer would leave the pipe running into the insulation.
                        CutRemainingLayers(doc, item, source);
                    }
                    catch (Exception ex)
                    {
                        item.Status = "failed";
                        item.Note = $"Проём создать не удалось: {ex.Message}";
                        result.FailedCount++;
                    }
                }

                tx.Commit();
            }
        }

        /// <summary>
        /// Cuts the same rectangle through the remaining layers of the assembly. Each
        /// layer is reported on its own: one of them failing is worth knowing about, and
        /// is not a reason to call the whole opening a failure.
        /// </summary>
        private void CutRemainingLayers(Document doc, MepOpeningPlanItem item, FamilySymbol source)
        {
            foreach (var layer in item.AlsoCuts ?? new List<MepOpeningLayerCut>())
            {
                try
                {
                    // The layer is cut at the size of the row, not at its own reading:
                    // the hole has to be the same rectangle all the way through.
                    var proxy = new MepOpeningPlanItem
                    {
                        Mark = item.Mark,
                        HostElementId = layer.HostElementId,
                        HostKind = item.HostKind,
                        WidthMm = item.WidthMm,
                        HeightMm = item.HeightMm,
                        CentreMm = layer.CentreMm
                    };

                    var created = item.HostKind == "floor"
                        ? CreateFloorOpening(doc, proxy)
                        : CreateWallOpening(doc, proxy, source);

                    layer.Status = created == null ? "failed" : "created";
                    if (created != null)
                        layer.OpeningElementId = created.Id.GetValue();
                }
                catch (Exception ex)
                {
                    layer.Status = "failed";
                    item.Note = $"Слой {layer.HostElementId} не прорезан: {ex.Message}";
                }
            }
        }

        private Element CreateWallOpening(Document doc, MepOpeningPlanItem item, FamilySymbol source)
        {
            var wall = doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(item.HostElementId)) as Wall;
            if (wall == null)
            {
                item.Note = "Стена не найдена — возможно, её изменили после превью.";
                return null;
            }

            var centre = ToFeet(item.CentreMm);

            if (source != null)
            {
                var symbol = OpeningTypeSizer.EnsureSizedSymbol(
                    doc, source, item.WidthMm, item.HeightMm, 5,
                    $"Проём {Math.Round(item.WidthMm)}x{Math.Round(item.HeightMm)}", out var error);

                if (symbol == null)
                {
                    item.Note = $"Типоразмер проёма не подобран: {error}";
                    return null;
                }

                var level = doc.GetElement(wall.LevelId) as Level;
                var instance = doc.Create.NewFamilyInstance(
                    centre, symbol, wall, level, StructuralType.NonStructural);

                SetMark(instance, item);
                return instance;
            }

            // No family to place: cut a native opening instead. It holds no mark and will
            // not appear in a ведомость, and the caller is told so rather than left to
            // discover an empty schedule.
            var profile = BuildWallProfile(wall, item, centre);
            if (profile == null)
            {
                item.Note = "Стена криволинейная — обычный проём по прямоугольнику в ней не построить. " +
                            "Передайте openingTypeId, чтобы ставить семейство.";
                return null;
            }

            var opening = doc.Create.NewOpening(wall, profile, true);
            item.Note = "Создан обычный проём без семейства: марка и ведомость по нему не соберутся. " +
                        "Передайте openingTypeId, чтобы ставить семейство.";
            return opening;
        }

        private static Element CreateFloorOpening(Document doc, MepOpeningPlanItem item)
        {
            var floor = doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(item.HostElementId));
            if (floor == null)
            {
                item.Note = "Перекрытие не найдено — возможно, его изменили после превью.";
                return null;
            }

            var centre = ToFeet(item.CentreMm);
            var halfW = RevitUnitConversion.FromMillimeters(item.WidthMm / 2.0);
            var halfH = RevitUnitConversion.FromMillimeters(item.HeightMm / 2.0);

            var p0 = new XYZ(centre.X - halfW, centre.Y - halfH, centre.Z);
            var p1 = new XYZ(centre.X + halfW, centre.Y - halfH, centre.Z);
            var p2 = new XYZ(centre.X + halfW, centre.Y + halfH, centre.Z);
            var p3 = new XYZ(centre.X - halfW, centre.Y + halfH, centre.Z);

            var profile = new CurveArray();
            profile.Append(Line.CreateBound(p0, p1));
            profile.Append(Line.CreateBound(p1, p2));
            profile.Append(Line.CreateBound(p2, p3));
            profile.Append(Line.CreateBound(p3, p0));

            return doc.Create.NewOpening(floor, profile, true);
        }

        /// <summary>
        /// Rectangle in the plane of the wall, for a native opening. Built along the wall
        /// direction rather than along project X: a wall running north-south would
        /// otherwise get a degenerate profile and Revit would refuse it.
        /// </summary>
        private static CurveArray BuildWallProfile(Wall wall, MepOpeningPlanItem item, XYZ centre)
        {
            var frame = BuildFrame(wall, out _);
            if (frame == null)
                return null;

            var halfW = RevitUnitConversion.FromMillimeters(item.WidthMm / 2.0);
            var halfH = RevitUnitConversion.FromMillimeters(item.HeightMm / 2.0);

            var along = frame.Value.U.Multiply(halfW);
            var up = frame.Value.V.Multiply(halfH);

            var p0 = centre.Subtract(along).Subtract(up);
            var p1 = centre.Add(along).Subtract(up);
            var p2 = centre.Add(along).Add(up);
            var p3 = centre.Subtract(along).Add(up);

            var profile = new CurveArray();
            profile.Append(Line.CreateBound(p0, p1));
            profile.Append(Line.CreateBound(p1, p2));
            profile.Append(Line.CreateBound(p2, p3));
            profile.Append(Line.CreateBound(p3, p0));
            return profile;
        }

        private static void SetMark(Element element, MepOpeningPlanItem item)
        {
            try
            {
                var mark = element?.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                if (mark != null && !mark.IsReadOnly)
                    mark.Set(item.Mark);
            }
            catch
            {
                // A hole without a mark is still a hole; the ведомость will show it blank.
            }
        }

        private static FamilySymbol ResolveOpeningSymbol(Document doc, long openingTypeId)
        {
            if (openingTypeId <= 0)
                return null;

            try
            {
                return doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(openingTypeId)) as FamilySymbol;
            }
            catch
            {
                return null;
            }
        }

        // --- odds and ends -------------------------------------------------------

        private static List<Solid> ReadSolids(Element element)
        {
            var solids = new List<Solid>();
            if (element == null)
                return solids;

            try
            {
                var geometry = element.get_Geometry(new Options
                {
                    DetailLevel = ViewDetailLevel.Medium,
                    ComputeReferences = false,
                    IncludeNonVisibleObjects = false
                });

                Harvest(geometry, solids, 0);
            }
            catch
            {
                // An element whose geometry Revit refuses to compute is skipped.
            }

            return solids;
        }

        private static void Harvest(GeometryElement geometry, List<Solid> solids, int depth)
        {
            if (geometry == null || depth > 4)
                return;

            foreach (var item in geometry)
            {
                switch (item)
                {
                    case Solid solid when solid.Volume > MinSolidVolume && solid.Faces.Size > 0:
                        solids.Add(solid);
                        break;
                    case GeometryInstance instance:
                        Harvest(instance.GetInstanceGeometry(), solids, depth + 1);
                        break;
                }
            }
        }

        private static Outline BuildOutline(Solid solid)
        {
            try
            {
                var box = solid.GetBoundingBox();
                if (box == null)
                    return null;

                var min = box.Transform.OfPoint(box.Min);
                var max = box.Transform.OfPoint(box.Max);

                return new Outline(
                    new XYZ(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z)),
                    new XYZ(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z)));
            }
            catch
            {
                return null;
            }
        }

        private static ElementId FindLevelId(Document doc, string levelName)
        {
            if (string.IsNullOrWhiteSpace(levelName))
                return null;

            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, levelName, StringComparison.CurrentCultureIgnoreCase));

            return level?.Id;
        }

        private static string ReadTypeName(Document doc, Element element)
        {
            try
            {
                var typeId = element?.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId)
                    return null;

                return doc?.GetElement(typeId)?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static string ReadLevelName(Document doc, Element element, out double elevationMm)
        {
            elevationMm = 0;

            try
            {
                var levelId = element?.LevelId;
                if (levelId == null || levelId == ElementId.InvalidElementId)
                    return null;

                if (!(doc.GetElement(levelId) is Level level))
                    return null;

                elevationMm = RevitUnitConversion.ToMillimeters(level.Elevation);
                return level.Name;
            }
            catch
            {
                return null;
            }
        }

        internal static string BuildMessage(CreateMepOpeningsResult result)
        {
            if (result == null)
                return string.Empty;

            var scanned = result.Links?.Count(link => link.Scanned) ?? 0;
            if (scanned == 0)
                return "Не по чему собирать задание: ни одной загруженной связи ИОС не найдено.";

            if (result.TotalOpenings == 0)
                return "Пересечений инженерных систем с нашими стенами и перекрытиями не найдено — " +
                       "отверстия не нужны.";

            var message = $"Отверстий: {result.TotalOpenings}";
            if (result.RawCrossings > result.TotalOpenings)
                message += $" (пересечений {result.RawCrossings}, пачки труб и слои стен объединены)";

            message += $". Запас {Math.Round(result.ClearanceMm)} мм, размеры кратны " +
                       $"{Math.Round(result.SizeStepMm)} мм.";

            if (result.SkippedNotThrough > 0)
            {
                // Without this line the смежник's runs look forgotten, when in fact they
                // were considered and judged not to need a hole.
                message += $" Не сквозных задеваний (идут вдоль, отверстие не нужно): " +
                           $"{result.SkippedNotThrough}.";
            }

            if (!result.Applied)
            {
                message += " Это превью — модель не тронута. Проверьте список и повторите с apply: true.";
                if (result.SkippedExistingCount > 0)
                    message += $" Из них уже существует: {result.SkippedExistingCount}.";
                return message;
            }

            message += $" Создано: {result.CreatedCount}";
            if (result.SkippedExistingCount > 0)
                message += $", уже было: {result.SkippedExistingCount}";
            if (result.FailedCount > 0)
                message += $", не удалось: {result.FailedCount}";
            message += ".";

            return message;
        }

        private static JZPoint ToMillimetres(XYZ point) => new JZPoint(
            Math.Round(RevitUnitConversion.ToMillimeters(point.X), 1),
            Math.Round(RevitUnitConversion.ToMillimeters(point.Y), 1),
            Math.Round(RevitUnitConversion.ToMillimeters(point.Z), 1));

        private static XYZ ToFeet(JZPoint point) => new XYZ(
            RevitUnitConversion.FromMillimeters(point?.X ?? 0),
            RevitUnitConversion.FromMillimeters(point?.Y ?? 0),
            RevitUnitConversion.FromMillimeters(point?.Z ?? 0));

        public string GetName() => "Create MEP Openings";
    }
}
