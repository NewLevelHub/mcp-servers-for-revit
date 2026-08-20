using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    /// <summary>
    /// Lists the Revit links of the open model and puts their geometry into our
    /// coordinates (REV-166) — the read every «смежники» tool is built on.
    /// </summary>
    /// <remarks>
    /// Read-only in both directions. No transaction is opened on the host document,
    /// and nothing is written into a link: a linked file belongs to another office,
    /// and this add-in must never be the reason their model changed.
    ///
    /// Three things decided the shape of this handler — the risky questions of REV-166:
    ///
    /// 1. **Coordinates.** A link inserted anywhere but 0,0 stores its elements in its
    ///    own numbers. <c>GetTotalTransform()</c> is the only correct bridge, so it is
    ///    applied here, and the samples show one element in both systems: the offset
    ///    can be checked against Revit instead of taken on trust.
    /// 2. **Speed.** The default pass is deliberately cheap — a status, a transform and
    ///    <c>GetElementCount()</c>, which materialises no elements at all. The expensive
    ///    readings (per-category counts, coordinate samples) are opt-in, and levelName
    ///    cuts the count to one floor the way <see cref="LevelScopeHelper"/> does for
    ///    rooms. Every link is timed and the total is reported, so the cost on a real
    ///    ИОС file is measured by running the command rather than guessed.
    /// 3. **Broken links.** An unloaded link returns null from <c>GetLinkDocument()</c>,
    ///    and a missing one throws somewhere inside the file reference. Every link is
    ///    read inside its own try/catch and lands in the report with a status either
    ///    way: one re-pathed subcontractor file must not cost the architect the list.
    /// </remarks>
    public class GetLinkedModelsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private bool _includeElementCounts = true;
        private bool _includeCategories;
        private int _categoryLimit = 8;
        private int _coordinateSamples = 1;
        private string _levelName = string.Empty;
        private string _nameFilter = string.Empty;

        /// <summary>Elements looked at before giving up on finding a placed one.</summary>
        private const int SampleScanLimit = 5000;

        public GetLinkedModelsResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        public void SetParameters(
            bool includeElementCounts = true,
            bool includeCategories = false,
            int categoryLimit = 8,
            int coordinateSamples = 1,
            string levelName = "",
            string nameFilter = "")
        {
            _includeElementCounts = includeElementCounts;
            _includeCategories = includeCategories;
            _categoryLimit = Math.Max(1, categoryLimit);
            _coordinateSamples = Math.Max(0, Math.Min(20, coordinateSamples));
            _levelName = levelName ?? string.Empty;
            _nameFilter = nameFilter ?? string.Empty;
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

                var instances = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .WhereElementIsNotElementType()
                    .Cast<RevitLinkInstance>()
                    .ToList();

                var links = new List<LinkedModelInfo>();
                foreach (var instance in instances)
                {
                    var info = ReadLink(doc, instance);
                    if (info != null)
                        links.Add(info);
                }

                links = links
                    .OrderBy(link => link.Name, StringComparer.CurrentCulture)
                    .ToList();

                total.Stop();

                ResultInfo = new GetLinkedModelsResult
                {
                    Success = true,
                    HostModel = doc.Title ?? string.Empty,
                    TotalLinks = links.Count,
                    LoadedCount = links.Count(link => link.IsReadable),
                    UnloadedCount = links.Count(link => IsUnloaded(link.Status)),
                    BrokenCount = links.Count(link => IsBroken(link.Status)),
                    ElapsedMs = total.ElapsedMilliseconds,
                    Links = links
                };
                ResultInfo.Message = BuildMessage(ResultInfo);
            }
            catch (Exception ex)
            {
                total.Stop();
                ResultInfo = new GetLinkedModelsResult
                {
                    Success = false,
                    ElapsedMs = total.ElapsedMilliseconds,
                    Message = $"Не удалось прочитать связи модели: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        /// <summary>
        /// Everything known about one link. Returns null only when the name filter rules
        /// it out; any failure below that is reported as a link carrying a status.
        /// </summary>
        private LinkedModelInfo ReadLink(Document doc, RevitLinkInstance instance)
        {
            var perLink = Stopwatch.StartNew();

            var info = new LinkedModelInfo
            {
                InstanceId = instance.Id.GetValue(),
                TypeId = instance.GetTypeId().GetValue()
            };

            try
            {
                var linkType = doc.GetElement(instance.GetTypeId()) as RevitLinkType;
                info.Name = FirstNonEmpty(linkType?.Name, instance.Name);

                if (!string.IsNullOrWhiteSpace(_nameFilter) &&
                    info.Name.IndexOf(_nameFilter, StringComparison.CurrentCultureIgnoreCase) < 0)
                {
                    return null;
                }

                var discipline = LinkDisciplineClassifier.Classify(info.Name);
                info.Section = discipline.Display;
                info.SectionFrom = discipline.IsKnown ? discipline.MatchedToken : null;

                info.Path = ReadPath(linkType);
                info.IsNested = ReadIsNested(linkType);

                var status = ReadStatus(linkType);
                info.Status = status;
                info.StatusText = DescribeStatus(status);

                info.Placement = ReadPlacement(instance);

                var linkDoc = instance.GetLinkDocument();
                if (linkDoc == null)
                {
                    // The normal outcome for an unloaded link and the only outcome for a
                    // missing one. Both are answers, not failures. A link that says Loaded
                    // and still gives no document is the third case — a closed workset,
                    // typically — and it gets its own wording rather than being called
                    // unloaded, because the fix is a different one.
                    info.IsReadable = false;
                    info.Note =
                        IsBroken(status)
                            ? "Файл связи не найден по указанному пути — содержимое прочитать нельзя."
                            : IsUnloaded(status)
                                ? "Связь выгружена — содержимое прочитать нельзя, пока её не загрузят."
                                : "Содержимое связи недоступно — например, закрыт её рабочий набор.";
                    return info;
                }

                info.IsReadable = true;
                ReadContent(linkDoc, instance, info);
                return info;
            }
            catch (Exception ex)
            {
                info.IsReadable = false;
                if (string.IsNullOrEmpty(info.Status))
                {
                    info.Status = "Error";
                    info.StatusText = "Ошибка чтения связи";
                }
                info.Note = $"Связь не прочитана: {ex.Message}";
                return info;
            }
            finally
            {
                perLink.Stop();
                info.ElapsedMs = perLink.ElapsedMilliseconds;
            }
        }

        /// <summary>Counts, per-category breakdown and coordinate samples inside a loaded link.</summary>
        private void ReadContent(Document linkDoc, RevitLinkInstance instance, LinkedModelInfo info)
        {
            var levelId = FindLevelId(linkDoc, _levelName);
            var levelScoped = levelId != null && levelId != ElementId.InvalidElementId;

            if (!string.IsNullOrWhiteSpace(_levelName) && !levelScoped)
            {
                // The link numbers its floors its own way («Этаж 01» against our «1 этаж»),
                // so a name that does not resolve is ordinary — say so and count it whole.
                info.Note = $"Уровень «{_levelName}» в этой связи не найден — счёт по всей связи.";
            }

            var scopeId = levelScoped ? levelId : null;

            if (_includeElementCounts)
            {
                info.ElementCount = BuildCollector(linkDoc, scopeId).GetElementCount();
                info.ElementCountScope = levelScoped ? $"уровень «{_levelName}»" : "вся связь";
            }

            if (_includeCategories)
            {
                info.Categories = ReadCategories(linkDoc, scopeId);
            }

            if (_coordinateSamples > 0)
            {
                info.Samples = ReadSamples(linkDoc, instance, scopeId);
            }
        }

        /// <summary>
        /// Model elements of a link: no types, no view-specific graphics. Built fresh for
        /// every pass — a collector is a one-shot query, and reusing one is what makes the
        /// second pass come back silently empty.
        /// </summary>
        private static FilteredElementCollector BuildCollector(Document linkDoc, ElementId levelId)
        {
            var collector = new FilteredElementCollector(linkDoc)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent();

            if (levelId != null && levelId != ElementId.InvalidElementId)
                collector = collector.WherePasses(new ElementLevelFilter(levelId));

            return collector;
        }

        private List<LinkCategoryCount> ReadCategories(Document linkDoc, ElementId levelId)
        {
            var counts = new Dictionary<string, int>(StringComparer.CurrentCulture);

            foreach (var element in BuildCollector(linkDoc, levelId))
            {
                var category = element?.Category?.Name;
                if (string.IsNullOrWhiteSpace(category))
                    continue;

                counts.TryGetValue(category, out var current);
                counts[category] = current + 1;
            }

            return counts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.CurrentCulture)
                .Take(_categoryLimit)
                .Select(pair => new LinkCategoryCount { Category = pair.Key, Count = pair.Value })
                .ToList();
        }

        /// <summary>
        /// A few placed elements, each shown in link coordinates and in ours. This is what
        /// makes an offset link checkable: the architect reads hostPointMm off the report
        /// and the same element off Revit, and they either agree or the transform is wrong.
        /// </summary>
        private List<LinkCoordinateSample> ReadSamples(
            Document linkDoc,
            RevitLinkInstance instance,
            ElementId levelId)
        {
            var transform = instance.GetTotalTransform();
            var samples = new List<LinkCoordinateSample>();
            var scanned = 0;

            foreach (var element in BuildCollector(linkDoc, levelId))
            {
                if (samples.Count >= _coordinateSamples || scanned >= SampleScanLimit)
                    break;
                scanned++;

                if (!(element?.Location is LocationPoint location) || location.Point == null)
                    continue;

                var linkPoint = location.Point;

                samples.Add(new LinkCoordinateSample
                {
                    ElementId = element.Id.GetValue(),
                    Category = element.Category?.Name ?? string.Empty,
                    LinkPointMm = ToMillimetres(linkPoint),
                    HostPointMm = ToMillimetres(transform.OfPoint(linkPoint))
                });
            }

            return samples;
        }

        private static LinkPlacementInfo ReadPlacement(RevitLinkInstance instance)
        {
            var transform = instance.GetTotalTransform();
            var basisX = transform.BasisX;

            return new LinkPlacementInfo
            {
                OriginMm = ToMillimetres(transform.Origin),
                RotationDegrees = Math.Round(Math.Atan2(basisX.Y, basisX.X) * 180.0 / Math.PI, 4),
                Mirrored = transform.Determinant < 0,
                OriginShared = transform.IsIdentity
            };
        }

        /// <summary>Level of the linked document by name — a link names its floors its own way.</summary>
        private static ElementId FindLevelId(Document linkDoc, string levelName)
        {
            if (string.IsNullOrWhiteSpace(levelName))
                return null;

            var level = new FilteredElementCollector(linkDoc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, levelName, StringComparison.CurrentCultureIgnoreCase));

            return level?.Id;
        }

        private static string ReadPath(RevitLinkType linkType)
        {
            if (linkType == null)
                return null;

            try
            {
                if (!linkType.IsExternalFileReference())
                    return null;

                var reference = linkType.GetExternalFileReference();
                // A missing link still remembers where it was looked for, and that path is
                // exactly what whoever re-paths it has to see.
                var modelPath = reference.GetAbsolutePath();
                if (modelPath == null || modelPath.Empty)
                    modelPath = reference.GetPath();

                return ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);
            }
            catch
            {
                return null;
            }
        }

        private static bool? ReadIsNested(RevitLinkType linkType)
        {
            try
            {
                return linkType?.IsNestedLink;
            }
            catch
            {
                return null;
            }
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
        /// Matched on the name rather than on enum members: Revit adds values to
        /// <c>LinkedFileStatus</c> between versions, and this add-in builds for 2020
        /// through 2026 off one source.
        /// </summary>
        internal static string DescribeStatus(string status)
        {
            switch (status)
            {
                case "Loaded":
                    return "Загружена";
                case "Unloaded":
                    return "Выгружена";
                case "LocallyUnloaded":
                    return "Выгружена локально";
                case "NotFound":
                    return "Файл не найден";
                case "Invalid":
                    return "Ссылка повреждена";
                case "Error":
                    return "Ошибка чтения связи";
                default:
                    return status;
            }
        }

        internal static bool IsUnloaded(string status) =>
            status == "Unloaded" || status == "LocallyUnloaded";

        internal static bool IsBroken(string status) =>
            status == "NotFound" || status == "Invalid" || status == "Error";

        internal static string BuildMessage(GetLinkedModelsResult result)
        {
            if (result.TotalLinks == 0)
                return "В открытой модели нет связанных файлов Revit.";

            var message = $"Связей: {result.TotalLinks}, загружено {result.LoadedCount}";
            if (result.UnloadedCount > 0)
                message += $", выгружено {result.UnloadedCount}";
            if (result.BrokenCount > 0)
                message += $", не найдено {result.BrokenCount}";

            return $"{message}. Обход занял {result.ElapsedMs} мс.";
        }

        private static JZPoint ToMillimetres(XYZ point) => new JZPoint(
            Math.Round(RevitUnitConversion.ToMillimeters(point.X), 1),
            Math.Round(RevitUnitConversion.ToMillimeters(point.Y), 1),
            Math.Round(RevitUnitConversion.ToMillimeters(point.Z), 1));

        private static string FirstNonEmpty(string preferred, string fallback) =>
            !string.IsNullOrWhiteSpace(preferred) ? preferred : fallback ?? string.Empty;

        public string GetName() => "Get Linked Models";
    }
}
