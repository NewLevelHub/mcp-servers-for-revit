using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Recorder;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;
using ElementIdExtensions = RevitMCPCommandSet.Utils.ElementIdExtensions;

namespace RevitMCPCommandSet.Services.Recorder
{
    /// <summary>
    /// REV-177: replays a recording (plugin/Core/Recorder/ActionRecorder.cs) on one or more
    /// other levels. Reads the same JSON file the plugin wrote — no project reference between
    /// the two assemblies, the shared %USERPROFILE%\.mcp-servers-for-revit\recordings\ directory
    /// and matching field names ARE the contract (see ReplayRecordingModels.cs's own header).
    ///
    /// Scope, stated plainly rather than silently assumed:
    /// - Only "create" actions with usable geometry are replayed (walls with a straight-line
    ///   location; point-based family instances). "modify" of a PRE-EXISTING element and
    ///   "delete" are recorded for the summary but never replayed — this handler only ever
    ///   creates new elements and sets parameters on elements it just created; it never edits
    ///   or deletes anything that was already on the target level, which is the safety property
    ///   that makes "replay on 14 levels unattended" defensible at all.
    /// - A hosted family instance (door/window) whose host wall is itself part of the SAME
    ///   recording resolves the host from what was (or would be) just created; a host outside
    ///   the recording falls back to the nearest existing wall on the target level within 50mm
    ///   of the original host's own (unshifted — only level changes for walls) midpoint. Neither
    ///   path found → the action fails with a stated reason, never a silent skip.
    /// - Walls: only X/Y from the recorded curve are kept; Z is rebuilt at 0 and the target
    ///   level + recorded base offset drive vertical placement, matching how this codebase's own
    ///   wall creation (CreateLineElementEventHandler) already works. Point-based instances DO
    ///   shift by the source→target level elevation delta, because NewFamilyInstance takes an
    ///   absolute point and Revit does not re-derive it from a level the way Wall.Create does.
    /// </summary>
    public class ReplayRecordingEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private string _recordingId;
        private List<string> _targetLevelNames;
        private int? _fromFloor;
        private int? _toFloor;
        private bool _confirm;

        public ReplayRecordingResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        private const double HostMatchToleranceFeet = 0.164; // ~50 mm
        private const double DefaultWallHeightFeet = 9.84; // ~3 m fallback if a recorded wall somehow lost its height

        public static string RecordingsDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mcp-servers-for-revit",
            "recordings");

        public void SetParameters(string recordingId, List<string> targetLevelNames, int? fromFloor, int? toFloor, bool confirm)
        {
            _recordingId = recordingId;
            _targetLevelNames = targetLevelNames;
            _fromFloor = fromFloor;
            _toFloor = toFloor;
            _confirm = confirm;
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 30000) => _resetEvent.WaitOne(timeoutMilliseconds);

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document
                    ?? throw new InvalidOperationException("No active Revit document.");

                var recipe = LoadRecipe(_recordingId);
                if (recipe == null)
                {
                    ResultInfo = new ReplayRecordingResult
                    {
                        Success = false,
                        Message = $"Запись «{_recordingId}» не найдена.",
                    };
                    return;
                }

                Level sourceLevel = null;
                if (recipe.SourceLevelId.HasValue)
                    sourceLevel = doc.GetElement(ElementIdExtensions.FromLong(recipe.SourceLevelId.Value)) as Level;

                if (sourceLevel == null)
                {
                    ResultInfo = new ReplayRecordingResult
                    {
                        Success = false,
                        RecordingId = recipe.Id,
                        RecordingName = recipe.Name,
                        Message = "Исходный уровень записи не найден в текущей модели — повтор невозможен.",
                    };
                    return;
                }

                var targetLevels = ResolveTargetLevels(doc);
                if (targetLevels.Count == 0)
                {
                    ResultInfo = new ReplayRecordingResult
                    {
                        Success = false,
                        RecordingId = recipe.Id,
                        RecordingName = recipe.Name,
                        Message = "Целевые уровни не найдены — проверьте targetLevelNames или fromFloor/toFloor.",
                    };
                    return;
                }

                var recipeCreatedIds = recipe.Actions
                    .Where(a => a.Kind == "create" && a.UnsupportedReason == null)
                    .Select(a => a.ElementId)
                    .ToHashSet();

                var levelResults = targetLevels
                    .Select(targetLevel => ReplayOnLevel(doc, recipe, sourceLevel, targetLevel, recipeCreatedIds))
                    .ToList();

                var created = levelResults.Sum(l => l.CreatedCount);
                var failed = levelResults.Sum(l => l.FailedCount);

                ResultInfo = new ReplayRecordingResult
                {
                    Success = true,
                    Applied = _confirm,
                    RecordingId = recipe.Id,
                    RecordingName = recipe.Name,
                    RecipeSummary = recipe.SummaryText,
                    Levels = levelResults,
                    Message = _confirm
                        ? $"Повторено на {levelResults.Count} уровнях: создано {created}, не удалось {failed}."
                        : $"Проба на {levelResults.Count} уровнях: будет создано {created}, не выполнится {failed}. Подтвердите confirm=true, чтобы применить.",
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new ReplayRecordingResult { Success = false, Message = $"Не удалось повторить запись: {ex.Message}" };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private static RecordedRecipeModel LoadRecipe(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            var path = Path.Combine(RecordingsDirectory, $"{id}.json");
            if (!File.Exists(path)) return null;
            return JsonConvert.DeserializeObject<RecordedRecipeModel>(File.ReadAllText(path));
        }

        private List<Level> ResolveTargetLevels(Document doc)
        {
            if (_fromFloor.HasValue && _toFloor.HasValue)
                return LevelScopeHelper.ResolveLevelsInRange(doc, _fromFloor.Value, _toFloor.Value);

            if (_targetLevelNames is { Count: > 0 })
            {
                var allLevels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                return _targetLevelNames
                    .Select(name => allLevels.FirstOrDefault(l => LevelScopeHelper.LevelNamesMatch(l.Name, name)))
                    .Where(l => l != null)
                    .ToList();
            }

            return new List<Level>();
        }

        private ReplayLevelResult ReplayOnLevel(
            Document doc,
            RecordedRecipeModel recipe,
            Level sourceLevel,
            Level targetLevel,
            HashSet<long> recipeCreatedIds)
        {
            var elevationDelta = targetLevel.Elevation - sourceLevel.Elevation;
            var idMap = new Dictionary<long, ElementId>();
            var actionResults = new List<ReplayActionResult>();

            Transaction transaction = null;
            if (_confirm)
            {
                transaction = new Transaction(doc, $"REV-177: повтор записи «{recipe.Name}» на уровне {targetLevel.Name}");
                transaction.Start();
            }

            try
            {
                foreach (var action in recipe.Actions)
                {
                    var result = new ReplayActionResult
                    {
                        ElementId = action.ElementId,
                        Kind = action.Kind,
                        Category = action.Category,
                        TypeName = action.TypeName,
                    };

                    if (action.Kind != "create")
                    {
                        result.Reason = action.Kind == "delete"
                            ? "Удаления не повторяются."
                            : "Изменения существующих (не созданных в этой записи) элементов не повторяются на другом уровне.";
                        actionResults.Add(result);
                        continue;
                    }

                    if (action.UnsupportedReason != null)
                    {
                        result.Reason = action.UnsupportedReason;
                        actionResults.Add(result);
                        continue;
                    }

                    try
                    {
                        ElementId newId;
                        if (action.Curve != null)
                        {
                            newId = ReplayWall(doc, action, targetLevel);
                        }
                        else if (action.Point != null)
                        {
                            newId = ReplayFamilyInstance(doc, action, targetLevel, elevationDelta, idMap, recipeCreatedIds, out var reason);
                            if (newId == null) result.Reason = reason;
                        }
                        else
                        {
                            newId = null;
                            result.Reason = "Не удалось определить геометрию для повтора.";
                        }

                        if (newId != null)
                        {
                            result.Success = true;
                            idMap[action.ElementId] = newId;
                            if (_confirm)
                            {
                                result.NewElementId = newId.GetValue();
                                ApplyTrackedParameters(doc, newId, action.Parameters);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Reason = ex.Message;
                    }

                    actionResults.Add(result);
                }

                transaction?.Commit();
            }
            catch
            {
                if (transaction is { } t && t.HasStarted() && !t.HasEnded())
                    t.RollBack();
                throw;
            }

            return new ReplayLevelResult
            {
                LevelName = targetLevel.Name,
                LevelId = targetLevel.Id.GetValue(),
                CreatedCount = actionResults.Count(r => r.Success),
                FailedCount = actionResults.Count(r => !r.Success),
                Actions = actionResults,
            };
        }

        /// <summary>
        /// Returns InvalidElementId (not null) in preview mode to mean "would succeed" — callers
        /// only dereference it as a real id when _confirm is true. Only X/Y are kept from the
        /// recorded curve; Z is rebuilt at 0 and the target level + recorded base offset carry
        /// the vertical placement, matching CreateLineElementEventHandler's own convention.
        /// </summary>
        private ElementId ReplayWall(Document doc, RecordedActionModel action, Level targetLevel)
        {
            var wallType = ResolveType(doc, action.TypeId, action.TypeName) as WallType;
            if (wallType == null)
                throw new InvalidOperationException($"Тип стены «{action.TypeName}» не найден в текущей модели.");
            if (action.Curve?.Start == null || action.Curve.End == null)
                throw new InvalidOperationException("У записанного действия нет геометрии стены.");

            if (!_confirm) return ElementId.InvalidElementId;

            var start = new XYZ(action.Curve.Start.X, action.Curve.Start.Y, 0);
            var end = new XYZ(action.Curve.End.X, action.Curve.End.Y, 0);
            if (start.DistanceTo(end) < 1e-6)
                throw new InvalidOperationException("Записанная стена имеет нулевую длину.");
            var line = Line.CreateBound(start, end);

            var height = ParseFeet(action.Parameters, "__wallHeightFeet", DefaultWallHeightFeet);
            var offset = ParseFeet(action.Parameters, "__wallBaseOffsetFeet", 0.0);

            var wall = Wall.Create(doc, line, wallType.Id, targetLevel.Id, height, offset, false, false);
            return wall?.Id;
        }

        private ElementId ReplayFamilyInstance(
            Document doc,
            RecordedActionModel action,
            Level targetLevel,
            double elevationDelta,
            Dictionary<long, ElementId> idMap,
            HashSet<long> recipeCreatedIds,
            out string failureReason)
        {
            failureReason = null;

            var symbol = ResolveType(doc, action.TypeId, action.TypeName) as FamilySymbol;
            if (symbol == null)
            {
                failureReason = $"Тип «{action.TypeName}» не найден в текущей модели.";
                return null;
            }

            ElementId hostId = null;
            var hosted = action.HostElementId.HasValue;
            if (hosted)
            {
                var originalHostId = action.HostElementId!.Value;
                if (idMap.TryGetValue(originalHostId, out var mapped))
                {
                    hostId = mapped;
                }
                else if (recipeCreatedIds.Contains(originalHostId))
                {
                    // Actions replay in recorded (time) order, so a host recorded earlier should
                    // already be in idMap — this only trips on a corrupt/hand-edited recipe.
                    failureReason = "Элемент-хозяин ещё не создан в этой записи (нарушен порядок действий).";
                    return null;
                }
                else
                {
                    hostId = FindExistingHostOnLevel(doc, originalHostId, targetLevel);
                    if (hostId == null)
                    {
                        failureReason = $"Не найдена стена-хозяин на уровне «{targetLevel.Name}».";
                        return null;
                    }
                }
            }

            if (!_confirm) return ElementId.InvalidElementId;

            if (!symbol.IsActive) symbol.Activate();

            var point = new XYZ(action.Point.X, action.Point.Y, action.Point.Z + elevationDelta);

            FamilyInstance instance;
            if (hosted && hostId != null && hostId != ElementId.InvalidElementId)
            {
                var hostElement = doc.GetElement(hostId);
                instance = doc.Create.NewFamilyInstance(point, symbol, hostElement, targetLevel, StructuralType.NonStructural);
            }
            else
            {
                instance = doc.Create.NewFamilyInstance(point, symbol, targetLevel, StructuralType.NonStructural);
                // Hosted instances are auto-oriented by Revit from their host wall — only apply
                // the recorded rotation for non-hosted ones (furniture etc.), where it is the
                // only source of that orientation.
                if (action.Rotation.HasValue && Math.Abs(action.Rotation.Value) > 1e-9
                    && instance.Location is LocationPoint locationPoint)
                {
                    var axis = Line.CreateBound(point, point + XYZ.BasisZ);
                    locationPoint.Rotate(axis, action.Rotation.Value);
                }
            }

            return instance?.Id;
        }

        /// <summary>Read-only match by 2D midpoint against every wall already on the target level — used when a hosted instance's original host was not itself part of the recording (a pre-existing wall).</summary>
        private ElementId FindExistingHostOnLevel(Document doc, long originalHostId, Level targetLevel)
        {
            if (doc.GetElement(ElementIdExtensions.FromLong(originalHostId)) is not Wall originalHost) return null;
            if (originalHost.Location is not LocationCurve originalLocation) return null;
            if (originalLocation.Curve is not Line originalLine) return null;

            var midpoint = originalLine.Evaluate(0.5, true);

            Wall best = null;
            var bestDistance = double.MaxValue;

            var candidates = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .Cast<Wall>()
                .Where(w => w.LevelId == targetLevel.Id);

            foreach (var candidate in candidates)
            {
                if (candidate.Location is not LocationCurve candidateLocation) continue;
                if (candidateLocation.Curve is not Line candidateLine) continue;

                var candidateMid = candidateLine.Evaluate(0.5, true);
                var distance = Math.Sqrt(Math.Pow(candidateMid.X - midpoint.X, 2) + Math.Pow(candidateMid.Y - midpoint.Y, 2));
                if (distance < HostMatchToleranceFeet && distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }

            return best?.Id;
        }

        private static Element ResolveType(Document doc, long? typeId, string typeName)
        {
            if (typeId.HasValue)
            {
                var byId = doc.GetElement(ElementIdExtensions.FromLong(typeId.Value));
                if (byId != null) return byId;
            }

            if (string.IsNullOrWhiteSpace(typeName)) return null;

            return new FilteredElementCollector(doc)
                .WhereElementIsElementType()
                .FirstOrDefault(e => TypeDisplayName(e) == typeName);
        }

        private static string TypeDisplayName(Element type) => type switch
        {
            WallType wallType => wallType.Name,
            FamilySymbol familySymbol => $"{familySymbol.FamilyName}: {familySymbol.Name}",
            _ => type.Name,
        };

        private static void ApplyTrackedParameters(Document doc, ElementId elementId, Dictionary<string, string> parameters)
        {
            var element = doc.GetElement(elementId);
            if (element == null || parameters == null) return;

            if (parameters.TryGetValue("Mark", out var mark) && !string.IsNullOrEmpty(mark))
            {
                var parameter = element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                if (parameter is { IsReadOnly: false }) parameter.Set(mark);
            }

            if (parameters.TryGetValue("Comments", out var comments) && !string.IsNullOrEmpty(comments))
            {
                var parameter = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (parameter is { IsReadOnly: false }) parameter.Set(comments);
            }
        }

        private static double ParseFeet(Dictionary<string, string> parameters, string key, double fallback)
        {
            if (parameters != null
                && parameters.TryGetValue(key, out var raw)
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
            return fallback;
        }

        public string GetName() => "Replay Recording";
    }
}
