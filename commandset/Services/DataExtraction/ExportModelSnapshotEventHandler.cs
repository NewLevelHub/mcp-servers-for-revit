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
    /// Reads the open model one page at a time so the server can write a snapshot of
    /// it into SQLite (REV-170) — the ground floor of «что изменилось с прошлой выдачи».
    /// </summary>
    /// <remarks>
    /// Read-only: no transaction is opened and nothing is written back to the model.
    ///
    /// Three decisions shape this handler:
    ///
    /// 1. **Pages, not one answer.** 300 000 elements do not fit a socket frame — the
    ///    cap is 50 MB — and holding the whole snapshot in memory on both sides to
    ///    move it once would be worse. The server asks for a page, writes it, asks for
    ///    the next; that is also what makes the write batched, which is what the
    ///    ticket's minute-not-half-an-hour budget needs.
    ///
    /// 2. **The element list is built once and cached.** Deciding *which* elements
    ///    belong in a snapshot needs the category of each one, and a category cannot be
    ///    read without materialising the element — so the filter is a full pass over
    ///    the model. Paying it on all sixty pages would triple the snapshot. It is paid
    ///    on the first page, sorted by id so pages never overlap or skip, and handed
    ///    back as <c>snapshotToken</c>. A token that changes mid-run means the model
    ///    moved under us, and the server reports that rather than stitching two
    ///    different models into one snapshot.
    ///
    /// 3. **Stable keys, not localised ones.** Category and parameter names come back
    ///    as <c>BuiltInCategory</c> / <c>BuiltInParameter</c> names. Revit here comes up
    ///    Russian one session and English the next, and a snapshot keyed on the labels
    ///    would diff as "the whole model was rewritten" against one taken the day
    ///    before. The labels travel too, once per page, for the report to read from.
    /// </remarks>
    public class ExportModelSnapshotEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private int _offset;
        private int _limit = 5000;
        private bool _includeAnnotation;
        private bool _includeRooms = true;
        private bool _includeBoundingBox = true;
        private bool _includeServiceCategories;
        private List<string> _categories = new List<string>();
        private List<string> _extraParameters = new List<string>();
        private string _requestedToken = string.Empty;

        /// <summary>
        /// The element list a run is cut into pages from. Static: the handler instance
        /// is recreated per command, but the pages of one snapshot are separate calls.
        /// </summary>
        private static List<long> _cachedIds;
        private static string _cachedScopeKey = string.Empty;
        private static string _cachedToken = string.Empty;
        private static DateTime _cachedAt = DateTime.MinValue;

        /// <summary>
        /// How long a cached element list stays usable. Long enough for a snapshot of a
        /// very large model, short enough that a list from this morning is never quietly
        /// reused this afternoon.
        /// </summary>
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Spatial elements are the point of the whole эпик («площадь квартиры выросла»),
        /// so they are kept whatever <c>CategoryType</c> Revit files them under.
        /// </summary>
        private static readonly HashSet<int> AlwaysKept = new HashSet<int>
        {
            (int)BuiltInCategory.OST_Rooms,
            (int)BuiltInCategory.OST_Areas,
            (int)BuiltInCategory.OST_MEPSpaces,
        };

        /// <summary>
        /// Categories Revit files as Model but which are not the building.
        /// </summary>
        /// <remarks>
        /// Measured on «Короткий блок» 21.08.2026: without this, 10 325 of 27 162 rows —
        /// 38 % of the snapshot — were these, and 9 576 of them were <c>OST_SketchLines</c>
        /// alone. They are not just bulk, they are a worse answer: the sketch lines of a
        /// перекрытие are its own internals, so reshaping one slab would come back as
        /// thirty changed lines instead of one changed floor — whose area and volume the
        /// snapshot already records. Materials and property sets are not elements of the
        /// building at all; cameras, sun paths, legend previews and sheets are the
        /// documentation apparatus, and sheets have <c>check_sheet_readiness</c> of their
        /// own.
        ///
        /// Deliberately narrow. Anything that could be design intent stays: room
        /// separation lines and area boundaries are how an architect draws a room that
        /// has no walls, and the base points stay because a moved setting-out point is
        /// exactly the change nobody may miss. <c>includeServiceCategories</c> brings
        /// them all back for whoever needs them.
        /// </remarks>
        private static readonly HashSet<int> ServiceCategories = new HashSet<int>
        {
            // The sketch behind a floor, roof, opening or stair — internals of an
            // element that is itself in the snapshot.
            (int)BuiltInCategory.OST_SketchLines,
            (int)BuiltInCategory.OST_StairsSketchBoundaryLines,
            (int)BuiltInCategory.OST_StairsSketchPathLines,
            // Not elements of the building.
            (int)BuiltInCategory.OST_Materials,
            (int)BuiltInCategory.OST_PropertySet,
            (int)BuiltInCategory.OST_ProjectInformation,
            // The documentation apparatus, not what it documents.
            (int)BuiltInCategory.OST_Sheets,
            (int)BuiltInCategory.OST_PreviewLegendComponents,
            (int)BuiltInCategory.OST_Cameras,
            (int)BuiltInCategory.OST_SunStudy,
        };

        public ModelSnapshotPage ResultInfo { get; private set; } = new ModelSnapshotPage();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetParameters(
            int offset,
            int limit,
            bool includeAnnotation,
            bool includeRooms,
            bool includeBoundingBox,
            bool includeServiceCategories,
            List<string> categories,
            List<string> extraParameters,
            string snapshotToken)
        {
            _offset = Math.Max(0, offset);
            _limit = Math.Max(1, Math.Min(20000, limit));
            _includeAnnotation = includeAnnotation;
            _includeRooms = includeRooms;
            _includeBoundingBox = includeBoundingBox;
            _includeServiceCategories = includeServiceCategories;
            _categories = categories ?? new List<string>();
            _extraParameters = extraParameters ?? new List<string>();
            _requestedToken = snapshotToken ?? string.Empty;
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
            long scanElapsed = 0;

            try
            {
                // Revit answers the socket before it has opened anything, and for a
                // 100 MB model that window is a minute wide. Without this the caller
                // gets «Object reference not set to an instance of an object» — which
                // says nothing about the one thing they need to do (21.08.2026: cost a
                // whole acceptance run to work out).
                var uiDoc = app.ActiveUIDocument;
                if (uiDoc?.Document == null)
                {
                    throw new InvalidOperationException(
                        "в Revit не открыта модель. Откройте файл и повторите — " +
                        "сразу после запуска Revit модель ещё грузится.");
                }

                var doc = uiDoc.Document;

                var wanted = ResolveCategoryFilter(doc, out var unresolved);
                var scopeKey = BuildScopeKey(doc, wanted);

                var ids = ReuseCachedIds(scopeKey)
                          ?? BuildIdList(doc, wanted, scopeKey, out scanElapsed);

                var elements = ReadPage(doc, ids, out var labels);

                ResultInfo = new ModelSnapshotPage
                {
                    Success = true,
                    ModelName = doc.Title ?? string.Empty,
                    ModelPath = doc.PathName ?? string.Empty,
                    RevitVersion = app.Application.VersionNumber ?? string.Empty,
                    TotalElements = ids.Count,
                    Offset = _offset,
                    Count = elements.Count,
                    HasMore = _offset + elements.Count < ids.Count,
                    SnapshotToken = _cachedToken,
                    ParameterLabels = labels,
                    Elements = elements,
                    ScanElapsedMs = scanElapsed,
                    Message = BuildMessage(ids.Count, elements.Count, unresolved),
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new ModelSnapshotPage
                {
                    Success = false,
                    Message = $"Снимок модели не снят: {ex.Message}",
                };
            }
            finally
            {
                total.Stop();
                ResultInfo.ElapsedMs = total.ElapsedMilliseconds;
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Export Model Snapshot";

        // --- element list -------------------------------------------------------

        /// <summary>
        /// The cached list, when this call is a later page of the same run against the
        /// same model and the same filter. Any doubt returns null and the list is
        /// rebuilt — a wrong reuse would silently snapshot the wrong elements.
        /// </summary>
        private List<long> ReuseCachedIds(string scopeKey)
        {
            if (_offset == 0) return null;
            if (_cachedIds == null) return null;
            if (!string.Equals(_cachedScopeKey, scopeKey, StringComparison.Ordinal)) return null;
            if (!string.IsNullOrEmpty(_requestedToken) &&
                !string.Equals(_requestedToken, _cachedToken, StringComparison.Ordinal)) return null;
            if (DateTime.UtcNow - _cachedAt > CacheLifetime) return null;

            return _cachedIds;
        }

        /// <summary>
        /// One pass over the model, keeping what belongs in a snapshot. Sorted by id so
        /// that paging is stable: the same offset always cuts the list in the same place.
        /// </summary>
        private List<long> BuildIdList(
            Document doc,
            HashSet<int> wanted,
            string scopeKey,
            out long scanElapsedMs)
        {
            var scan = Stopwatch.StartNew();
            var ids = new List<long>();

            foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (!Keep(element, wanted)) continue;
                ids.Add(element.Id.GetValue());
            }

            ids.Sort();
            scan.Stop();
            scanElapsedMs = scan.ElapsedMilliseconds;

            _cachedIds = ids;
            _cachedScopeKey = scopeKey;
            _cachedToken = Guid.NewGuid().ToString("N").Substring(0, 12);
            _cachedAt = DateTime.UtcNow;

            return ids;
        }

        /// <summary>
        /// What a snapshot is of: model geometry, plus rooms and areas. View-specific
        /// elements are annotation — a dimension redrawn on a sheet is not a change to
        /// the building, and on a documented model they outnumber what is.
        /// </summary>
        private bool Keep(Element element, HashSet<int> wanted)
        {
            var category = element.Category;
            if (category == null) return false;

            var categoryId = (int)category.Id.GetValue();

            // An explicit category list is taken at its word — someone who asked for
            // OST_SketchLines by name wants them.
            if (wanted.Count > 0) return wanted.Contains(categoryId);

            if (!_includeServiceCategories && ServiceCategories.Contains(categoryId)) return false;

            if (AlwaysKept.Contains(categoryId)) return true;
            if (_includeAnnotation) return true;

            if (element.ViewSpecific) return false;
            return category.CategoryType == CategoryType.Model;
        }

        /// <summary>
        /// Category ids asked for. Names resolve through <see cref="CategoryResolver"/>
        /// so «Стены» and "Walls" both work on a machine whose Revit changes language,
        /// and a <c>BuiltInCategory</c> name is accepted as well.
        /// </summary>
        private HashSet<int> ResolveCategoryFilter(Document doc, out List<string> unresolved)
        {
            var wanted = new HashSet<int>();
            unresolved = new List<string>();

            foreach (var name in _categories)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (name.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) &&
                    Enum.TryParse(name, true, out BuiltInCategory builtIn))
                {
                    wanted.Add((int)builtIn);
                    continue;
                }

                var category = CategoryResolver.Find(doc, name);
                if (category != null) wanted.Add((int)category.Id.GetValue());
                else unresolved.Add(name);
            }

            return wanted;
        }

        private string BuildScopeKey(Document doc, HashSet<int> wanted)
        {
            var categories = wanted.OrderBy(id => id).Select(id => id.ToString());
            var extras = _extraParameters.OrderBy(name => name, StringComparer.Ordinal);

            return string.Join("|", new[]
            {
                doc.PathName ?? string.Empty,
                doc.Title ?? string.Empty,
                _includeAnnotation ? "1" : "0",
                _includeServiceCategories ? "1" : "0",
                string.Join(",", categories),
                string.Join(",", extras),
            });
        }

        // --- one page -----------------------------------------------------------

        private List<ModelSnapshotElement> ReadPage(
            Document doc,
            List<long> ids,
            out Dictionary<string, string> labels)
        {
            labels = new Dictionary<string, string>(StringComparer.Ordinal);
            var page = new List<ModelSnapshotElement>();

            // Types and categories repeat across thousands of elements; resolving each
            // one once is most of the difference between a minute and ten.
            var typeCache = new Dictionary<long, ElementTypeNames>();
            var categoryCache = new Dictionary<int, string>();
            var levelCache = new Dictionary<long, string>();

            var last = Math.Min(ids.Count, _offset + _limit);

            for (var i = _offset; i < last; i++)
            {
                Element element;
                try
                {
                    element = doc.GetElement(Utils.ElementIdExtensions.FromLong(ids[i]));
                }
                catch
                {
                    continue;
                }

                // Deleted between the scan and this page. Skipping it is right: the
                // snapshot then simply does not contain an element that no longer exists.
                if (element == null) continue;

                try
                {
                    page.Add(Describe(doc, element, typeCache, categoryCache, levelCache, labels));
                }
                catch
                {
                    // One unreadable element must not cost the whole snapshot; it is
                    // left out, and the count in the answer is what was actually read.
                }
            }

            return page;
        }

        private ModelSnapshotElement Describe(
            Document doc,
            Element element,
            Dictionary<long, ElementTypeNames> typeCache,
            Dictionary<int, string> categoryCache,
            Dictionary<long, string> levelCache,
            Dictionary<string, string> labels)
        {
            var category = element.Category;
            var categoryId = category == null ? 0 : (int)category.Id.GetValue();

            var snapshot = new ModelSnapshotElement
            {
                ElementId = element.Id.GetValue(),
                UniqueId = element.UniqueId ?? string.Empty,
                CategoryKey = CategoryKey(categoryId, categoryCache),
                Category = category?.Name ?? string.Empty,
                LevelName = LevelName(doc, element, levelCache),
            };

            var typeId = element.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                var names = TypeNames(doc, typeId, typeCache);
                snapshot.TypeId = typeId.GetValue();
                snapshot.FamilyName = names.Family;
                snapshot.TypeName = names.Type;
            }

            if (_includeBoundingBox)
            {
                snapshot.BoundingBoxMm = ReadBoundingBox(element);
            }

            if (_includeRooms)
            {
                ReadRoom(element, snapshot);
            }

            SnapshotParameterSet.Collect(doc, element, _extraParameters, snapshot.Parameters, labels);

            return snapshot;
        }

        private static ModelSnapshotBox ReadBoundingBox(Element element)
        {
            BoundingBoxXYZ box;
            try
            {
                // null view: the model box, not what some view happens to crop.
                box = element.get_BoundingBox(null);
            }
            catch
            {
                return null;
            }

            if (box == null) return null;

            return new ModelSnapshotBox
            {
                Min = ToMillimetres(box.Min),
                Max = ToMillimetres(box.Max),
            };
        }

        private static JZPoint ToMillimetres(XYZ point)
        {
            return new JZPoint(point.X * 304.8, point.Y * 304.8, point.Z * 304.8);
        }

        /// <summary>
        /// Which room the element stands in. Only family instances are asked: Revit
        /// keeps that relation for them, whereas working it out for a wall or a duct
        /// would mean a point-in-room test per element — the one reading that would
        /// blow the whole time budget.
        /// </summary>
        private static void ReadRoom(Element element, ModelSnapshotElement snapshot)
        {
            Room room = null;

            try
            {
                if (element is Room self)
                {
                    room = self;
                }
                else if (element is FamilyInstance instance)
                {
                    room = instance.Room;
                }
            }
            catch
            {
                return;
            }

            if (room == null) return;

            try
            {
                snapshot.RoomName = room.Name ?? string.Empty;
                snapshot.RoomNumber = room.Number ?? string.Empty;
            }
            catch
            {
                // An unplaced room throws on Name; it has no room to report either way.
            }
        }

        private static string CategoryKey(int categoryId, Dictionary<int, string> cache)
        {
            if (categoryId == 0) return string.Empty;
            if (cache.TryGetValue(categoryId, out var cached)) return cached;

            var key = Enum.IsDefined(typeof(BuiltInCategory), categoryId)
                ? ((BuiltInCategory)categoryId).ToString()
                // A category of the project's own making has no built-in name; the id
                // is stable within the file, which is all a diff of one model needs.
                : $"CAT_{categoryId}";

            cache[categoryId] = key;
            return key;
        }

        private static ElementTypeNames TypeNames(
            Document doc,
            ElementId typeId,
            Dictionary<long, ElementTypeNames> cache)
        {
            var key = typeId.GetValue();
            if (cache.TryGetValue(key, out var cached)) return cached;

            var names = new ElementTypeNames();
            try
            {
                if (doc.GetElement(typeId) is ElementType type)
                {
                    names.Family = type.FamilyName ?? string.Empty;
                    names.Type = type.Name ?? string.Empty;
                }
            }
            catch
            {
                // Leave the names empty rather than lose the element.
            }

            cache[key] = names;
            return names;
        }

        /// <summary>
        /// The named parameters a level can hide behind, in the order they are tried.
        /// </summary>
        /// <remarks>
        /// Each one was put here by an element that came back with no level on
        /// «Короткий блок» 21.08.2026. <c>INSTANCE_REFERENCE_LEVEL_PARAM</c> alone
        /// accounted for 3 900 of them — every beam in the model, because structural
        /// framing leaves <c>LevelId</c> invalid and keeps its floor under «Опорный
        /// уровень». That matters more than it looks: the comparison this snapshot
        /// exists for groups its answer by level, and «переставлено 12 балок» with no
        /// floor named is not an answer.
        /// </remarks>
        private static readonly BuiltInParameter[] LevelParameters =
        {
            BuiltInParameter.SCHEDULE_LEVEL_PARAM,
            BuiltInParameter.FAMILY_LEVEL_PARAM,
            BuiltInParameter.FAMILY_BASE_LEVEL_PARAM,
            BuiltInParameter.WALL_BASE_CONSTRAINT,
            BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
            BuiltInParameter.STAIRS_BASE_LEVEL_PARAM,
            BuiltInParameter.ROOF_BASE_LEVEL_PARAM,
            BuiltInParameter.GROUP_LEVEL,
        };

        /// <summary>
        /// The level an element is filed under. <c>LevelId</c> answers for most things;
        /// what it leaves blank is looked for in <see cref="LevelParameters"/>, and
        /// failing those in whatever parameter of the element actually points at a
        /// <see cref="Level"/>.
        /// </summary>
        /// <remarks>
        /// The last resort is a scan of the element's own parameters, and it is
        /// deliberately last: it costs a walk of the parameter set, and it only ever
        /// runs for the handful of categories the named list does not cover. It is
        /// there because the alternative is adding a <c>BuiltInParameter</c> to the list
        /// every time an unfamiliar category turns up blank, which is a list nobody
        /// finishes.
        /// </remarks>
        private static string LevelName(Document doc, Element element, Dictionary<long, string> cache)
        {
            var levelId = ElementId.InvalidElementId;

            try
            {
                levelId = element.LevelId;
            }
            catch
            {
                // Not every element type answers LevelId; the fallbacks below do.
            }

            if (levelId == null || levelId == ElementId.InvalidElementId)
            {
                levelId = LevelFromNamedParameters(element);
            }

            if (levelId == null || levelId == ElementId.InvalidElementId)
            {
                levelId = LevelFromAnyParameter(doc, element);
            }

            if (levelId == null || levelId == ElementId.InvalidElementId) return string.Empty;

            var key = levelId.GetValue();
            if (cache.TryGetValue(key, out var cached)) return cached;

            var name = doc.GetElement(levelId) is Level level ? level.Name ?? string.Empty : string.Empty;
            cache[key] = name;
            return name;
        }

        private static ElementId LevelFromNamedParameters(Element element)
        {
            foreach (var bip in LevelParameters)
            {
                Parameter parameter;
                try
                {
                    parameter = element.get_Parameter(bip);
                }
                catch
                {
                    continue;
                }

                if (parameter == null || parameter.StorageType != StorageType.ElementId) continue;

                var candidate = parameter.AsElementId();
                if (candidate != null && candidate != ElementId.InvalidElementId) return candidate;
            }

            return ElementId.InvalidElementId;
        }

        private static ElementId LevelFromAnyParameter(Document doc, Element element)
        {
            try
            {
                foreach (Parameter parameter in element.Parameters)
                {
                    if (parameter == null || parameter.StorageType != StorageType.ElementId) continue;

                    var candidate = parameter.AsElementId();
                    if (candidate == null || candidate == ElementId.InvalidElementId) continue;

                    if (doc.GetElement(candidate) is Level) return candidate;
                }
            }
            catch
            {
                // An element whose parameter set cannot be walked simply has no level.
            }

            return ElementId.InvalidElementId;
        }

        private static string BuildMessage(int total, int count, List<string> unresolved)
        {
            var message = $"Прочитано элементов: {count} из {total}";
            if (unresolved.Count > 0)
            {
                message += $". Категории не найдены: {string.Join(", ", unresolved)}";
            }

            return message;
        }

        private class ElementTypeNames
        {
            public string Family = string.Empty;
            public string Type = string.Empty;
        }
    }
}
