using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    /// <summary>
    /// Creates Annotate → Filled Region («Цветовая область») from room boundaries
    /// on the active floor plan. This is a detail annotation, not Color Fill Scheme
    /// and not room Override Graphics.
    /// </summary>
    public class CreateFilledRegionsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        public const string DefaultCommentTag = "MCP-FR";

        private UIApplication _uiApp;
        private Document Doc => _uiApp.ActiveUIDocument.Document;

        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        private List<long> _roomIds = new();
        private List<string> _roomNames = new();
        private string _filledRegionTypeName = string.Empty;
        private string _colorPreset = "red";
        private bool _clearPrevious = true;
        private bool _clearOnly;
        private string _commentTag = DefaultCommentTag;

        public object ResultInfo { get; private set; }

        public void SetParameters(
            IEnumerable<long> roomIds,
            IEnumerable<string> roomNames,
            string filledRegionTypeName,
            string colorPreset,
            bool clearPrevious,
            string commentTag,
            bool clearOnly = false)
        {
            _roomIds = (roomIds ?? Enumerable.Empty<long>()).Where(id => id > 0).Distinct().ToList();
            _roomNames = (roomNames ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _filledRegionTypeName = filledRegionTypeName?.Trim() ?? string.Empty;
            _colorPreset = string.IsNullOrWhiteSpace(colorPreset) ? "red" : colorPreset.Trim().ToLowerInvariant();
            _clearPrevious = clearPrevious;
            _clearOnly = clearOnly;
            _commentTag = string.IsNullOrWhiteSpace(commentTag) ? DefaultCommentTag : commentTag.Trim();
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName() => "Create Filled Regions";

        public void Execute(UIApplication uiapp)
        {
            _uiApp = uiapp;
            try
            {
                var view = _uiApp.ActiveUIDocument.ActiveView;
                if (view is not ViewPlan)
                {
                    ResultInfo = new
                    {
                        success = false,
                        message = $"Active view must be a floor plan. Got: {view.ViewType} «{view.Name}»."
                    };
                    return;
                }

                // clearOnly: remove prior MCP-FR regions without painting every room.
                if (_clearOnly)
                {
                    using (var tx = new Transaction(Doc, "MCP Clear Filled Regions"))
                    {
                        tx.Start();
                        var clearedIds = ClearPreviousRegions(view);
                        tx.Commit();
                        ResultInfo = new
                        {
                            success = true,
                            clearOnly = true,
                            view = view.Name,
                            commentTag = _commentTag,
                            createdCount = 0,
                            deletedPreviousCount = clearedIds.Count,
                            deletedPreviousIds = clearedIds
                        };
                    }
                    return;
                }

                var regionType = ResolveFilledRegionType();
                if (regionType == null)
                {
                    var available = new FilteredElementCollector(Doc)
                        .OfClass(typeof(FilledRegionType))
                        .Cast<FilledRegionType>()
                        .Select(t => t.Name)
                        .OrderBy(n => n)
                        .Take(40)
                        .ToList();

                    ResultInfo = new
                    {
                        success = false,
                        message =
                            "Filled Region type not found. Pass filledRegionTypeName " +
                            "(e.g. ADSK_У_Сплошная_Красный) or ensure a solid red type exists in the project.",
                        availableTypes = available
                    };
                    return;
                }

                var rooms = ResolveRooms(view);
                if (rooms.Count == 0 && !_clearPrevious)
                {
                    ResultInfo = new
                    {
                        success = false,
                        message = "Укажите roomIds или roomNames — без них заливка всех помещений на виде запрещена."
                    };
                    return;
                }

                var created = new List<object>();
                var deletedIds = new List<long>();
                var errors = new List<string>();

                using (var tx = new Transaction(Doc, "MCP Create Filled Regions"))
                {
                    tx.Start();

                    if (_clearPrevious)
                    {
                        deletedIds = ClearPreviousRegions(view);
                    }

                    var opt = new SpatialElementBoundaryOptions
                    {
                        SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
                    };

                    foreach (var room in rooms)
                    {
                        try
                        {
                            var loops = BuildBoundaryLoops(room, opt);
                            if (loops.Count == 0)
                            {
                                errors.Add($"{room.Id.GetValue()}: no closed boundary loops");
                                continue;
                            }

                            var fr = FilledRegion.Create(Doc, regionType.Id, view.Id, loops);
                            var comments = fr.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                            if (comments != null && !comments.IsReadOnly)
                            {
                                comments.Set(_commentTag);
                            }

                            created.Add(new
                            {
                                roomId = room.Id.GetValue(),
                                roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty,
                                regionId = fr.Id.GetValue(),
                                loops = loops.Count
                            });
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"{room.Id.GetValue()}: {ex.Message}");
                        }
                    }

                    tx.Commit();
                }

                ResultInfo = new
                {
                    success = created.Count > 0 || (rooms.Count == 0 && deletedIds.Count > 0),
                    view = view.Name,
                    filledRegionType = regionType.Name,
                    filledRegionTypeId = regionType.Id.GetValue(),
                    commentTag = _commentTag,
                    createdCount = created.Count,
                    deletedPreviousCount = deletedIds.Count,
                    created,
                    deletedPreviousIds = deletedIds,
                    errors
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new
                {
                    success = false,
                    message = ex.Message
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private List<Room> ResolveRooms(View view)
        {
            var onView = new FilteredElementCollector(Doc, view.Id)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(r => r != null && r.Area > 1e-9)
                .ToList();

            if (_roomIds.Count == 0 && _roomNames.Count == 0)
            {
                // Never paint every room on the plan when ids were omitted (common LLM mistake).
                return new List<Room>();
            }

            var byId = new HashSet<long>(_roomIds);
            var nameSet = new HashSet<string>(_roomNames, StringComparer.OrdinalIgnoreCase);

            var matched = onView
                .Where(r =>
                {
                    if (byId.Contains(r.Id.GetValue()))
                        return true;
                    var name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
                    return nameSet.Contains(name);
                })
                .ToList();

            // Also resolve ids that exist in the document but may be filtered out of the view collector
            foreach (var id in _roomIds)
            {
                if (matched.Any(r => r.Id.GetValue() == id))
                    continue;
                if (Doc.GetElement(Utils.ElementIdExtensions.FromLong(id)) is Room room && room.Area > 1e-9)
                    matched.Add(room);
            }

            return matched
                .GroupBy(r => r.Id.GetValue())
                .Select(g => g.First())
                .ToList();
        }

        private static List<CurveLoop> BuildBoundaryLoops(Room room, SpatialElementBoundaryOptions opt)
        {
            var loops = new List<CurveLoop>();
            var segments = room.GetBoundarySegments(opt);
            if (segments == null || segments.Count == 0)
                return loops;

            foreach (IList<BoundarySegment> loopSegs in segments)
            {
                if (loopSegs == null || loopSegs.Count < 3)
                    continue;

                try
                {
                    var loop = new CurveLoop();
                    foreach (BoundarySegment seg in loopSegs)
                    {
                        var curve = seg?.GetCurve();
                        if (curve != null)
                            loop.Append(curve);
                    }

                    if (!loop.IsOpen())
                        loops.Add(loop);
                }
                catch
                {
                    // skip malformed loop
                }
            }

            return loops;
        }

        private List<long> ClearPreviousRegions(View view)
        {
            var deleted = new List<long>();
            var regions = new FilteredElementCollector(Doc, view.Id)
                .OfClass(typeof(FilledRegion))
                .Cast<FilledRegion>()
                .Where(fr =>
                {
                    var c = fr.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? string.Empty;
                    return c.StartsWith(_commentTag, StringComparison.OrdinalIgnoreCase);
                })
                .Select(fr => fr.Id)
                .ToList();

            if (regions.Count == 0)
                return deleted;

            foreach (var id in regions)
                deleted.Add(id.GetValue());

            Doc.Delete(regions);
            return deleted;
        }

        private FilledRegionType ResolveFilledRegionType()
        {
            var types = new FilteredElementCollector(Doc)
                .OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>()
                .ToList();

            if (!string.IsNullOrEmpty(_filledRegionTypeName))
            {
                var exact = types.FirstOrDefault(t =>
                    t.Name.Equals(_filledRegionTypeName, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                    return exact;

                var partial = types.FirstOrDefault(t =>
                    t.Name.IndexOf(_filledRegionTypeName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (partial != null)
                    return partial;
            }

            // Prefer organization solid fills by color preset keywords
            var keywords = _colorPreset switch
            {
                "green" => new[] { "Сплошная_Зелен", "Solid_Green", "Зелен", "Green" },
                "blue" => new[] { "Сплошная_Синий", "Сплошная_Голуб", "Solid_Blue", "Синий", "Blue" },
                "grey" or "gray" => new[] { "Сплошная_Серый", "Solid_Gray", "Серый", "Grey", "Gray" },
                _ => new[] { "Сплошная_Красный", "Solid_Red", "Красный", "Red" }
            };

            foreach (var kw in keywords)
            {
                var hit = types.FirstOrDefault(t =>
                    t.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit != null)
                    return hit;
            }

            // Last resort: any type whose name contains Сплошная / Solid
            return types.FirstOrDefault(t =>
                       t.Name.IndexOf("Сплошная", StringComparison.OrdinalIgnoreCase) >= 0
                       || t.Name.IndexOf("Solid", StringComparison.OrdinalIgnoreCase) >= 0)
                   ?? types.FirstOrDefault();
        }
    }
}
