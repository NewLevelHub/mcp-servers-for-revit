using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace revit_mcp_plugin.Core.Recorder
{
    /// <summary>
    /// REV-177: subscribes to Application.DocumentChanged (registered once, unconditionally,
    /// in Application.cs — same pattern as DocumentSaved/Opened/Closed there) and, only while
    /// recording, turns the human's own manual edits into a replayable recipe. Recording never
    /// touches the model itself — it only reads what Revit already committed, so it cannot be
    /// the thing that breaks an architect's actual work; any capture failure is swallowed rather
    /// than surfaced, by design (see OnDocumentChanged).
    ///
    /// The plugin project has no reference to commandset (they talk only over the JSON-RPC
    /// socket), so replay's actual element-creation lives in commandset instead — this class
    /// only captures and stores. See docs/tool-registry.md's REV-177 section for the full split.
    /// </summary>
    public static class ActionRecorder
    {
        private static RecordedRecipe _current;
        private static HashSet<long> _createdInThisSession;

        public static bool IsRecording => _current != null;

        /// <summary>Mark and Comments — the two parameters the ticket's own example names ("1 марку"). Read by BuiltInParameter, not by display name, so a UI language flip mid-session (a known issue on this codebase) cannot silently stop capture.</summary>
        private static readonly BuiltInParameter[] TrackedParameters =
        {
            BuiltInParameter.ALL_MODEL_MARK,
            BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
        };

        private static readonly Dictionary<BuiltInParameter, string> TrackedParameterKeys = new Dictionary<BuiltInParameter, string>
        {
            [BuiltInParameter.ALL_MODEL_MARK] = "Mark",
            [BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS] = "Comments",
        };

        public static RecordedRecipe Start(Document doc, View activeView, string name)
        {
            long? levelId = null;
            string levelName = null;
            if (activeView is ViewPlan plan && plan.GenLevel != null)
            {
                levelId = plan.GenLevel.Id.ToLongId();
                levelName = plan.GenLevel.Name;
            }

            _current = new RecordedRecipe
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? $"Запись {DateTime.Now:dd.MM HH:mm}" : name,
                RecordedUtc = DateTime.UtcNow,
                SourceLevelId = levelId,
                SourceLevelName = levelName,
            };
            _createdInThisSession = new HashSet<long>();
            return _current;
        }

        /// <summary>Stops, builds the summary, saves to disk (survives a Revit restart), and returns the finished recipe. Null if nothing was being recorded.</summary>
        public static RecordedRecipe Stop()
        {
            if (_current == null) return null;

            var recipe = _current;
            recipe.SummaryText = BuildSummary(recipe);
            _current = null;
            _createdInThisSession = null;

            try
            {
                RecordingStore.Save(recipe);
            }
            catch
            {
                // A recipe that failed to save is still returned for the current-turn summary —
                // just won't survive a restart. Better than losing the whole session's work.
            }

            return recipe;
        }

        /// <summary>Discards the in-progress recording without saving.</summary>
        public static void Cancel()
        {
            _current = null;
            _createdInThisSession = null;
        }

        public static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (!IsRecording) return;

            try
            {
                var doc = e.GetDocument();
                foreach (var id in e.GetAddedElementIds()) CaptureCreate(doc, id);
                foreach (var id in e.GetModifiedElementIds()) CaptureModify(doc, id);
                foreach (var id in e.GetDeletedElementIds()) CaptureDelete(id);
            }
            catch
            {
                // Recording is a convenience feature — it must never be the reason a real
                // modeling transaction looks like it failed.
            }
        }

        private static void CaptureCreate(Document doc, ElementId id)
        {
            var longId = id.ToLongId();
            if (_createdInThisSession.Contains(longId)) return;

            var element = doc.GetElement(id);
            if (element == null) return;

            var action = new RecordedAction { Kind = "create", ElementId = longId };
            PopulateCommon(action, element);

            switch (element)
            {
                case Wall wall:
                    PopulateWall(action, wall);
                    break;
                case FamilyInstance fi:
                    PopulateFamilyInstance(action, fi);
                    break;
                default:
                    action.UnsupportedReason =
                        $"Тип элемента «{element.GetType().Name}» пока не поддерживается для повтора.";
                    break;
            }

            _current.Actions.Add(action);
            _createdInThisSession.Add(longId);
        }

        private static void CaptureModify(Document doc, ElementId id)
        {
            var longId = id.ToLongId();

            if (_createdInThisSession.Contains(longId))
            {
                // The element's own creation is already an action in this recipe — a follow-up
                // edit (e.g. typing a Mark right after drawing the wall) merges into it rather
                // than becoming a separate "modify" step, matching how a person would describe
                // it ("поставил стену с маркой Х", not "поставил стену, потом переименовал").
                var createAction = _current.Actions.FirstOrDefault(a => a.ElementId == longId && a.Kind == "create");
                if (createAction != null)
                {
                    var element = doc.GetElement(id);
                    if (element != null) CaptureTrackedParameters(createAction, element);
                }
                return;
            }

            var el = doc.GetElement(id);
            if (el == null) return;

            // Repeated edits to the same pre-existing element within one recording collapse
            // into one modify action, not N.
            var existing = _current.Actions.FirstOrDefault(a => a.ElementId == longId && a.Kind == "modify");
            var action = existing ?? new RecordedAction { Kind = "modify", ElementId = longId };
            PopulateCommon(action, el);
            CaptureTrackedParameters(action, el);
            if (existing == null) _current.Actions.Add(action);
        }

        private static void CaptureDelete(ElementId id)
        {
            var longId = id.ToLongId();

            if (_createdInThisSession.Contains(longId))
            {
                // Created then deleted within the same recording — net no-op, drop the create too.
                _current.Actions.RemoveAll(a => a.ElementId == longId);
                _createdInThisSession.Remove(longId);
                return;
            }

            // The element is already gone — doc.GetElement(id) returns null, nothing left to
            // read but the id. Recorded for the summary/count, but honestly marked as something
            // replay does not act on (see docs/tool-registry.md's REV-177 section for why).
            _current.Actions.Add(new RecordedAction
            {
                Kind = "delete",
                ElementId = longId,
                UnsupportedReason =
                    "Удаление уже существовавшего (не созданного в этой записи) элемента не повторяется автоматически.",
            });
        }

        private static void PopulateCommon(RecordedAction action, Element element)
        {
            var category = element.Category;
            action.Category = category?.Name;
            if (category != null)
            {
                try
                {
                    action.BuiltInCategory = ((BuiltInCategory)int.Parse(category.Id.ToString())).ToString();
                }
                catch
                {
                    // A category outside the BuiltInCategory enum (rare) — Category name alone still covers the summary.
                }
            }

            var typeId = element.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId)
                action.TypeId = typeId.ToLongId();

            if (element.LevelId != null && element.LevelId != ElementId.InvalidElementId)
            {
                action.LevelId = element.LevelId.ToLongId();
                action.LevelName = (element.Document.GetElement(element.LevelId) as Level)?.Name;
            }
        }

        private static void PopulateWall(RecordedAction action, Wall wall)
        {
            if (!(wall.Location is LocationCurve locationCurve) || !(locationCurve.Curve is Line line))
            {
                action.UnsupportedReason = "Изогнутая или не линейная стена — повтор пока не поддерживается.";
                return;
            }

            action.Curve = new RecordedCurve
            {
                Start = ToRecordedPoint(line.GetEndPoint(0)),
                End = ToRecordedPoint(line.GetEndPoint(1)),
            };
            action.TypeName = wall.WallType?.Name;

            var heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            if (heightParam != null && heightParam.HasValue)
                action.Parameters["__wallHeightFeet"] = heightParam.AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);

            var offsetParam = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
            if (offsetParam != null && offsetParam.HasValue)
                action.Parameters["__wallBaseOffsetFeet"] = offsetParam.AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void PopulateFamilyInstance(RecordedAction action, FamilyInstance familyInstance)
        {
            if (!(familyInstance.Location is LocationPoint locationPoint))
            {
                action.UnsupportedReason = "Элемент без точки размещения — повтор пока не поддерживается.";
                return;
            }

            action.Point = ToRecordedPoint(locationPoint.Point);
            action.Rotation = locationPoint.Rotation;
            if (familyInstance.Host != null)
                action.HostElementId = familyInstance.Host.Id.ToLongId();

            var symbol = familyInstance.Symbol;
            action.TypeId = symbol?.Id.ToLongId();
            action.TypeName = symbol != null ? $"{symbol.FamilyName}: {symbol.Name}" : null;
        }

        private static void CaptureTrackedParameters(RecordedAction action, Element element)
        {
            foreach (var builtIn in TrackedParameters)
            {
                var parameter = element.get_Parameter(builtIn);
                if (parameter == null) continue;
                var key = TrackedParameterKeys[builtIn];
                action.Parameters[key] = parameter.AsString() ?? string.Empty;
            }
        }

        private static RecordedPoint ToRecordedPoint(XYZ point) =>
            new RecordedPoint { X = point.X, Y = point.Y, Z = point.Z };

        /// <summary>
        /// A plain-language count by category, e.g. "4 × Стены, 2 × Двери, 1 × изменение параметров".
        /// Deliberately simple rather than grammatically perfect Russian pluralization (which
        /// varies by category and count) — clear and honest beats a poetic sentence that is
        /// wrong for some category.
        /// </summary>
        private static string BuildSummary(RecordedRecipe recipe)
        {
            var creates = recipe.Actions.Where(a => a.Kind == "create" && a.UnsupportedReason == null).ToList();
            var unsupported = recipe.Actions.Where(a => a.UnsupportedReason != null).ToList();
            var modifies = recipe.Actions.Where(a => a.Kind == "modify").ToList();
            var deletes = recipe.Actions.Where(a => a.Kind == "delete").ToList();

            var parts = new List<string>();
            foreach (var group in creates.GroupBy(a => a.Category ?? "объект").OrderByDescending(g => g.Count()))
            {
                parts.Add($"{group.Count()} × {group.Key}");
            }
            if (modifies.Count > 0) parts.Add($"{modifies.Count} × изменение параметров");
            if (unsupported.Count > 0) parts.Add($"{unsupported.Count} × не поддерживается для повтора");
            if (deletes.Count > 0) parts.Add($"{deletes.Count} × удаление (не повторяется)");

            return parts.Count == 0 ? "Ничего не записано." : "Записано: " + string.Join(", ", parts) + ".";
        }
    }

    /// <summary>
    /// The plugin project has no version-conditional REVIT2024_OR_GREATER build constants
    /// (unlike commandset/Utils/ElementIdExtensions.cs — plugin.csproj never needed one before
    /// this ticket), so ElementId.Value vs .IntegerValue can't be branched on here. ElementId's
    /// ToString() renders the same numeric id in both API generations regardless, which sidesteps
    /// the whole property-name difference for the read-only direction this class needs — it
    /// never reconstructs an ElementId from a stored long, only records one.
    /// </summary>
    internal static class ElementIdLongExtensions
    {
        public static long ToLongId(this ElementId id) =>
            id == null ? -1L : long.Parse(id.ToString());
    }
}
