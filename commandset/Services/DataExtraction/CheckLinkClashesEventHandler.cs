using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    /// <summary>
    /// Finds where the open model and its links run into each other (REV-167) — the
    /// «балка режет проём» question, answered as a list instead of a hunt.
    /// </summary>
    /// <remarks>
    /// Read-only in both directions, like <see cref="GetLinkedModelsEventHandler"/> it
    /// builds on: no transaction on the host document, nothing written into a link.
    ///
    /// Four decisions shape this handler:
    ///
    /// 1. **Coordinates.** Every element of a link is stored in the numbers of that link.
    ///    Rather than transforming thousands of link solids into ours, each host solid is
    ///    pushed once into link coordinates through <c>GetTotalTransform().Inverse</c> and
    ///    the search runs inside the link. The reported point comes back through the
    ///    forward transform, so what the architect reads is in OUR coordinates.
    /// 2. **Speed.** <c>ElementIntersectsSolidFilter</c> is a slow filter — Revit applies
    ///    it element by element. It is therefore always paired with a
    ///    <c>BoundingBoxIntersectsFilter</c> built from the same solid, which Revit runs
    ///    first off its spatial index, so the expensive test only ever sees a handful of
    ///    candidates.
    /// 3. **Depth.** The filter answers «они пересекаются», not «насколько». The overlap
    ///    body is computed once per hit and its smallest side is the depth — that is what
    ///    separates a 1 mm modelling slip from a beam through a wall, and it is what the
    ///    tolerance cuts on.
    /// 4. **Limits.** Real ИОС files produce hundreds of clashes, and a run that never
    ///    ends is worse than a partial answer. The scan stops on a clash cap or a time
    ///    budget and says so — <c>truncated</c> is part of the contract, not an error.
    /// </remarks>
    public class CheckLinkClashesEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private string _linkNameFilter = string.Empty;
        private List<string> _hostCategoryNames = new List<string>();
        private List<string> _linkCategoryNames = new List<string>();
        private double _toleranceMm = ClashRules.DefaultToleranceMm;
        private string _levelName = string.Empty;
        private int _maxClashes = 500;
        // A runaway guard, not the working limit. It used to be 5000, and a perfectly
        // ordinary panel house has 10473 elements in the default host categories — the
        // scan cut itself in half and reported truncated on a model nobody would call
        // large. The time budget is what ends a long scan; this only stops a runaway.
        private int _maxHostElements = 50000;
        private int _timeBudgetSeconds = 90;
        private bool _includeRooms = true;

        /// <summary>Solids thinner than this are modelling debris, not geometry (ft³).</summary>
        private const double MinSolidVolume = 1e-6;

        public CheckLinkClashesResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        public void SetParameters(
            string linkNameFilter = "",
            IEnumerable<string> hostCategories = null,
            IEnumerable<string> linkCategories = null,
            double toleranceMm = ClashRules.DefaultToleranceMm,
            string levelName = "",
            int maxClashes = 500,
            int maxHostElements = 50000,
            int timeBudgetSeconds = 90,
            bool includeRooms = true)
        {
            _linkNameFilter = linkNameFilter ?? string.Empty;
            _hostCategoryNames = (hostCategories ?? Enumerable.Empty<string>()).ToList();
            _linkCategoryNames = (linkCategories ?? Enumerable.Empty<string>()).ToList();
            _toleranceMm = ClashRules.NormaliseToleranceMm(toleranceMm);
            _levelName = levelName ?? string.Empty;
            _maxClashes = Math.Max(1, Math.Min(5000, maxClashes));
            _maxHostElements = Math.Max(1, Math.Min(100000, maxHostElements));
            // The plugin waits 180 s for this command; a budget past that would only be
            // cut off by the wait, and the caller would get a timeout instead of a list.
            _timeBudgetSeconds = Math.Max(5, Math.Min(150, timeBudgetSeconds));
            _includeRooms = includeRooms;
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
                var result = new CheckLinkClashesResult
                {
                    Success = true,
                    HostModel = doc.Title ?? string.Empty,
                    ToleranceMm = _toleranceMm
                };

                var hostCategories = ClashCategories.Resolve(
                    doc, _hostCategoryNames, ClashCategories.DefaultHost, out var hostUnresolved);
                result.HostCategories = ClashCategories.Describe(doc, hostCategories);

                var linkTargets = CollectLinks(doc, result);

                var linkCategories = ResolveLinkCategories(linkTargets, out var linkUnresolved);
                result.LinkCategories = ClashCategories.Describe(doc, linkCategories);

                var refusal = DescribeRefusal(hostCategories, hostUnresolved, linkCategories, linkUnresolved);
                if (refusal != null)
                {
                    total.Stop();
                    result.Success = false;
                    result.ElapsedMs = total.ElapsedMilliseconds;
                    result.Message = refusal;
                    ResultInfo = result;
                    return;
                }

                Scan(doc, result, linkTargets, hostCategories, linkCategories, total);

                total.Stop();
                result.ElapsedMs = total.ElapsedMilliseconds;
                result.TotalClashes = result.Clashes.Count;
                result.ByCategoryPair = ClashRules.Summarise(result.Clashes);

                // Deepest first: page one is then the page worth arguing about, and the
                // pagination on the server side does not have to understand severity.
                result.Clashes = result.Clashes
                    .OrderByDescending(clash => clash.DepthMm ?? double.MaxValue)
                    .ToList();

                result.Message = ClashRules.BuildMessage(result);
                ResultInfo = result;
            }
            catch (Exception ex)
            {
                total.Stop();
                ResultInfo = new CheckLinkClashesResult
                {
                    Success = false,
                    ElapsedMs = total.ElapsedMilliseconds,
                    Message = $"Не удалось проверить коллизии со связями: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        // --- links ---------------------------------------------------------------

        /// <summary>One link we can actually read, with everything the scan needs.</summary>
        private sealed class LinkTarget
        {
            public Document LinkDocument;
            public Transform ToHost;
            public Transform ToLink;
            public LinkClashScanInfo Info;
            public Stopwatch Timer = new Stopwatch();
        }

        /// <summary>
        /// Every link of the model lands in <c>result.Links</c> — the readable ones as
        /// targets, the rest as a row saying why not. An unloaded КР link silently
        /// skipped would read as «коллизий нет», which is the answer this tool must
        /// never give by accident.
        /// </summary>
        private List<LinkTarget> CollectLinks(Document doc, CheckLinkClashesResult result)
        {
            var targets = new List<LinkTarget>();

            var instances = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .WhereElementIsNotElementType()
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (var instance in instances)
            {
                var info = new LinkClashScanInfo { LinkInstanceId = instance.Id.GetValue() };

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
                        var status = ReadStatus(linkType);
                        info.Scanned = false;
                        info.Note = GetLinkedModelsEventHandler.IsBroken(status)
                            ? "Файл связи не найден — сверить не с чем."
                            : GetLinkedModelsEventHandler.IsUnloaded(status)
                                ? "Связь выгружена — загрузите её, иначе коллизии по ней не видны."
                                : "Содержимое связи недоступно — например, закрыт её рабочий набор.";
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

        private static string ReadStatus(RevitLinkType linkType)
        {
            if (linkType == null)
                return LinkedFileStatus.Invalid.ToString();

            try
            {
                return linkType.GetLinkedFileStatus().ToString();
            }
            catch
            {
                return LinkedFileStatus.Invalid.ToString();
            }
        }

        /// <summary>
        /// Link-side categories are resolved against a linked document, not ours: the
        /// file of a смежник may run in a different UI language, and its own spelling is
        /// the one that resolves there.
        /// </summary>
        private List<BuiltInCategory> ResolveLinkCategories(List<LinkTarget> targets, out List<string> unresolved)
        {
            var sample = targets.FirstOrDefault()?.LinkDocument;
            return ClashCategories.Resolve(sample, _linkCategoryNames, ClashCategories.DefaultLink, out unresolved);
        }

        /// <summary>
        /// Why a run cannot start, in the terms of the caller. Returns null when it can.
        /// </summary>
        private static string DescribeRefusal(
            List<BuiltInCategory> hostCategories,
            List<string> hostUnresolved,
            List<BuiltInCategory> linkCategories,
            List<string> linkUnresolved)
        {
            if (hostUnresolved.Count > 0 && hostCategories.Count == 0)
                return $"Категории модели не распознаны: {string.Join(", ", hostUnresolved)}. " +
                       "Проверять нечего — назовите категории иначе или не задавайте их вовсе.";

            if (linkUnresolved.Count > 0 && linkCategories.Count == 0)
                return $"Категории связи не распознаны: {string.Join(", ", linkUnresolved)}. " +
                       "Проверять нечего — назовите категории иначе или не задавайте их вовсе.";

            if (hostCategories.Count == 0 || linkCategories.Count == 0)
                return "Не задано, что с чем сверять: обе стороны остались пустыми.";

            return null;
        }

        // --- the scan ------------------------------------------------------------

        private void Scan(
            Document doc,
            CheckLinkClashesResult result,
            List<LinkTarget> targets,
            List<BuiltInCategory> hostCategories,
            List<BuiltInCategory> linkCategories,
            Stopwatch total)
        {
            var levelId = FindLevelId(doc, _levelName);
            var levelScoped = levelId != null && levelId != ElementId.InvalidElementId;
            result.Scope = levelScoped
                ? $"уровень «{_levelName}»"
                : string.IsNullOrWhiteSpace(_levelName)
                    ? "вся модель"
                    : $"вся модель (уровень «{_levelName}» не найден)";

            if (targets.Count == 0)
                return;

            var linkFilter = new ElementMulticategoryFilter(linkCategories);
            var hostElements = CollectHostElements(doc, hostCategories, levelScoped ? levelId : null);
            var budget = TimeSpan.FromSeconds(_timeBudgetSeconds);

            foreach (var element in hostElements)
            {
                if (result.HostElementsScanned >= _maxHostElements ||
                    result.Clashes.Count >= _maxClashes ||
                    total.Elapsed > budget)
                {
                    result.Truncated = true;
                    break;
                }

                var solids = ReadSolids(element);
                if (solids.Count == 0)
                    continue;

                result.HostElementsScanned++;

                foreach (var target in targets)
                {
                    target.Timer.Start();
                    try
                    {
                        ScanElementAgainstLink(doc, result, element, solids, target, linkFilter);
                    }
                    catch (Exception ex)
                    {
                        // One unreadable element must not cost the whole report. It is
                        // recorded on the link so a systematic failure is still visible.
                        if (string.IsNullOrEmpty(target.Info.Note))
                            target.Info.Note = $"Часть элементов не проверена: {ex.Message}";
                    }
                    finally
                    {
                        target.Timer.Stop();
                    }

                    if (result.Clashes.Count >= _maxClashes)
                    {
                        result.Truncated = true;
                        break;
                    }
                }
            }

            foreach (var target in targets)
                target.Info.ElapsedMs = target.Timer.ElapsedMilliseconds;
        }

        private void ScanElementAgainstLink(
            Document doc,
            CheckLinkClashesResult result,
            Element element,
            List<Solid> solids,
            LinkTarget target,
            ElementMulticategoryFilter linkFilter)
        {
            var seen = new HashSet<long>();

            foreach (var solid in solids)
            {
                var inLink = SolidUtils.CreateTransformed(solid, target.ToLink);
                var outline = BuildOutline(inLink);
                if (outline == null)
                    continue;

                var collector = new FilteredElementCollector(target.LinkDocument)
                    .WhereElementIsNotElementType()
                    .WherePasses(linkFilter)
                    // Quick filter first — Revit runs it off its spatial index and only
                    // then hands the survivors to the element-by-element solid test.
                    .WherePasses(new BoundingBoxIntersectsFilter(outline))
                    .WherePasses(new ElementIntersectsSolidFilter(inLink));

                foreach (var candidate in collector)
                {
                    if (candidate == null || !seen.Add(candidate.Id.GetValue()))
                        continue;

                    var clash = BuildClash(doc, element, candidate, inLink, target);
                    if (!ClashRules.IsReportable(clash.DepthMm, _toleranceMm))
                    {
                        result.IgnoredBelowTolerance++;
                        continue;
                    }

                    result.Clashes.Add(clash);
                    target.Info.ClashCount++;

                    if (result.Clashes.Count >= _maxClashes)
                        return;
                }
            }
        }

        /// <summary>
        /// One row of the report. Always produced: Revit's own filter already said these
        /// two meet, so the only open question is how deep — never whether.
        /// </summary>
        private LinkClashItem BuildClash(
            Document doc,
            Element hostElement,
            Element linkElement,
            Solid hostSolidInLink,
            LinkTarget target)
        {
            var clash = new LinkClashItem
            {
                HostElementId = hostElement.Id.GetValue(),
                HostCategory = hostElement.Category?.Name ?? string.Empty,
                HostType = ReadTypeName(doc, hostElement),
                HostLevel = ReadLevelName(doc, hostElement),
                LinkName = target.Info.LinkName,
                LinkSection = target.Info.Section,
                LinkInstanceId = target.Info.LinkInstanceId,
                LinkElementId = linkElement.Id.GetValue(),
                LinkCategory = linkElement.Category?.Name ?? string.Empty,
                LinkType = ReadTypeName(target.LinkDocument, linkElement)
            };

            var overlap = ComputeOverlap(hostSolidInLink, linkElement);
            var centroidInLink = overlap.Centroid ?? hostSolidInLink.ComputeCentroid();

            clash.DepthMm = overlap.DepthMm;
            clash.OverlapVolumeM3 = overlap.VolumeM3;
            if (overlap.DepthMm == null)
            {
                // Revit's own filter said these two meet, so the row stays. It is marked
                // unmeasured instead: a clash nobody can size is still a clash to look at.
                clash.Note = "Глубину пересечения измерить не удалось — булева операция не прошла.";
            }

            var hostPoint = target.ToHost.OfPoint(centroidInLink);
            clash.PointMm = ToMillimetres(hostPoint);

            if (_includeRooms)
                ReadRoom(doc, hostPoint, clash);

            return clash;
        }

        private readonly struct Overlap
        {
            public Overlap(XYZ centroid, double? depthMm, double? volumeM3)
            {
                Centroid = centroid;
                DepthMm = depthMm;
                VolumeM3 = volumeM3;
            }

            /// <summary>Centre of the overlap, in LINK coordinates.</summary>
            public XYZ Centroid { get; }

            public double? DepthMm { get; }

            public double? VolumeM3 { get; }
        }

        /// <summary>
        /// Intersects the two bodies and measures the result. The smallest side of the
        /// overlap is the depth: a beam crossing a 200 mm wall gives 200, a 1 mm graze
        /// gives 1, and that is exactly the number the tolerance has to cut on.
        /// </summary>
        private static Overlap ComputeOverlap(Solid hostSolidInLink, Element linkElement)
        {
            XYZ fallbackCentroid = null;
            Solid deepest = null;
            var deepestVolume = 0.0;

            // A family instance carries several solids — a duct fitting, a beam with a
            // haunch. Whichever of them bites deepest is the one worth reporting, so all
            // are measured rather than stopping at the first hit.
            foreach (var linkSolid in ReadSolids(linkElement))
            {
                try
                {
                    if (fallbackCentroid == null)
                        fallbackCentroid = linkSolid.ComputeCentroid();

                    var intersection = BooleanOperationsUtils.ExecuteBooleanOperation(
                        hostSolidInLink, linkSolid, BooleanOperationsType.Intersect);

                    if (intersection == null || intersection.Volume <= MinSolidVolume)
                        continue;

                    if (intersection.Volume > deepestVolume)
                    {
                        deepest = intersection;
                        deepestVolume = intersection.Volume;
                    }
                }
                catch
                {
                    // Boolean operations fail on self-intersecting and near-degenerate
                    // geometry, which is ordinary in the model of a subcontractor. The
                    // clash is real — the filter of Revit found it — so it is reported
                    // without a depth rather than dropped.
                }
            }

            if (deepest == null)
                return new Overlap(fallbackCentroid, null, null);

            return new Overlap(
                deepest.ComputeCentroid(),
                MeasureSmallestSide(deepest),
                Math.Round(RevitUnitConversion.ToCubicMeters(deepestVolume), 6));
        }

        /// <summary>Smallest side of the bounding box of the solid, mm.</summary>
        private static double? MeasureSmallestSide(Solid solid)
        {
            try
            {
                var box = solid.GetBoundingBox();
                if (box == null)
                    return null;

                var size = box.Max - box.Min;
                var smallest = Math.Min(size.X, Math.Min(size.Y, size.Z));
                return Math.Round(RevitUnitConversion.ToMillimeters(smallest), 1);
            }
            catch
            {
                return null;
            }
        }

        private static Outline BuildOutline(Solid solid)
        {
            try
            {
                var box = solid.GetBoundingBox();
                if (box == null)
                    return null;

                // GetBoundingBox is in the frame of the solid; the outline has to be in
                // document coordinates or the quick filter looks in the wrong place.
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

        // --- reading elements ----------------------------------------------------

        /// <summary>
        /// Solids of an element, including the ones inside a family instance. Empty for
        /// an element that carries no 3D body at all — annotation, a line, an opening cut.
        /// </summary>
        private static List<Solid> ReadSolids(Element element)
        {
            var solids = new List<Solid>();
            if (element == null)
                return solids;

            try
            {
                var options = new Options
                {
                    DetailLevel = ViewDetailLevel.Medium,
                    ComputeReferences = false,
                    IncludeNonVisibleObjects = false
                };

                var geometry = element.get_Geometry(options);
                if (geometry == null)
                    return solids;

                Harvest(geometry, solids, 0);
            }
            catch
            {
                // An element whose geometry Revit refuses to compute is skipped rather
                // than failing the run: on a real model there is always one.
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

        private static List<Element> CollectHostElements(
            Document doc,
            List<BuiltInCategory> categories,
            ElementId levelId)
        {
            var collector = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent()
                .WherePasses(new ElementMulticategoryFilter(categories));

            if (levelId != null && levelId != ElementId.InvalidElementId)
                collector = collector.WherePasses(new ElementLevelFilter(levelId));

            return collector.ToElements().ToList();
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

        private static string ReadLevelName(Document doc, Element element)
        {
            try
            {
                var levelId = element?.LevelId;
                if (levelId == null || levelId == ElementId.InvalidElementId)
                    return null;

                return (doc.GetElement(levelId) as Level)?.Name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The room the overlap falls in — «в коридоре на 3 этаже» is what makes a clash
        /// findable without opening a 3D view.
        /// </summary>
        private static void ReadRoom(Document doc, XYZ hostPoint, LinkClashItem clash)
        {
            if (hostPoint == null)
                return;

            try
            {
                var room = doc.GetRoomAtPoint(hostPoint) as Room;
                if (room == null)
                    return;

                clash.RoomName = room.Name;
                clash.RoomNumber = room.Number;
            }
            catch
            {
                // GetRoomAtPoint throws on models with no room volumes computed.
            }
        }

        private static JZPoint ToMillimetres(XYZ point) => new JZPoint(
            Math.Round(RevitUnitConversion.ToMillimeters(point.X), 1),
            Math.Round(RevitUnitConversion.ToMillimeters(point.Y), 1),
            Math.Round(RevitUnitConversion.ToMillimeters(point.Z), 1));

        public string GetName() => "Check Link Clashes";
    }
}
