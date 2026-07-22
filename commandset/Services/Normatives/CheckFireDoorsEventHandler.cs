using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Normatives;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Normatives
{
    /// <summary>
    /// Collects door facts from the active Revit model.
    /// Mark detection uses instance/type parameters AND door-schedule «Примечание» (REV-47).
    /// Normative rules are read from repo/normatives PDFs on the MCP server (REV-29).
    /// </summary>
    public class CheckFireDoorsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private static readonly string[] FireRatingParameterNames =
        {
            "ADSK_Предел огнестойкости экземпляра",
            "ADSK_Предел огнестойкости",
            "Fire Rating",
            "Противопожарность",
            "Огнестойкость",
            "EI",
            "Предел огнестойкости",
        };

        private static readonly string[] NoteParameterNames =
        {
            "BI_примечание",
            "ADSK_Примечание",
            "Примечание",
            "Type Comments",
            "Комментарии к типоразмеру",
            "Comments",
            "Комментарии",
        };

        private string _levelNameFilter = string.Empty;

        public CheckFireDoorsResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        public void SetParameters(string levelName = "")
        {
            _levelNameFilter = levelName ?? string.Empty;
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
                var doc = app.ActiveUIDocument.Document;
                var warnings = new List<string>();
                var scheduleRows = FireDoorScheduleNoteReader.ReadDoorScheduleNotes(doc);
                if (scheduleRows.Count == 0)
                {
                    warnings.Add(
                        "Спека с колонкой «Примечание» для дверей не найдена — ПД проверяются только по параметрам модели.");
                }

                var doorInstances = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .Where(OpeningFillClassifier.IsSchedulableDoor)
                    .ToList();

                var items = new List<DoorFireFacts>();

                foreach (var door in doorInstances)
                {
                    var levelName = doc.GetElement(door.LevelId)?.Name ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(_levelNameFilter) &&
                        !string.Equals(levelName, _levelNameFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var fromRoom = door.FromRoom?.Name ?? string.Empty;
                    var toRoom = door.ToRoom?.Name ?? string.Empty;
                    var familyName = door.Symbol?.Family?.Name ?? string.Empty;
                    var typeName = door.Symbol?.Name ?? string.Empty;

                    var parameterRating = ReadParameterFireRating(door);
                    var fromParameter = IsMarkedFromParameters(door, parameterRating);

                    var typeNote = ReadNoteParameters(door);
                    if (FireDoorScheduleNoteReader.IsPlaceholderNote(typeNote))
                        typeNote = string.Empty;

                    var scheduleNote = FireDoorScheduleNoteReader.FindNoteForDoor(
                        scheduleRows,
                        familyName,
                        typeName);
                    if (string.IsNullOrWhiteSpace(scheduleNote))
                        scheduleNote = typeNote;
                    else if (FireDoorScheduleNoteReader.IsPlaceholderNote(scheduleNote))
                        scheduleNote = FireDoorScheduleNoteReader.LooksLikeFireDoorText(typeNote)
                            ? typeNote
                            : string.Empty;

                    var fromScheduleNote = FireDoorScheduleNoteReader.LooksLikeFireDoorText(scheduleNote);
                    var isMarked = fromParameter || fromScheduleNote;
                    var markSource = FireDoorScheduleNoteReader.ClassifyMarkSource(
                        fromParameter,
                        fromScheduleNote);

                    var currentRating = !string.IsNullOrWhiteSpace(parameterRating)
                        ? parameterRating
                        : fromScheduleNote
                            ? ExtractFireRatingSnippet(scheduleNote)
                            : string.Empty;

                    items.Add(new DoorFireFacts
                    {
                        Id = door.Id.GetValue(),
                        UniqueId = door.UniqueId,
                        Mark = door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? string.Empty,
                        Family = familyName,
                        Type = typeName,
                        Level = levelName,
                        FromRoom = fromRoom,
                        ToRoom = toRoom,
                        OpeningWidthMm = GetDoorWidthMm(door),
                        IsOnEgressPath = IsOnEgressPath(fromRoom, toRoom, door),
                        IsMarkedAsFireDoor = isMarked,
                        MarkSource = markSource,
                        CurrentFireRating = currentRating,
                        ScheduleNote = scheduleNote ?? string.Empty,
                    });
                }

                ResultInfo = new CheckFireDoorsResult
                {
                    Success = true,
                    Message = $"Collected fire-door facts for {items.Count} doors"
                        + (scheduleRows.Count > 0
                            ? $" (schedule notes: {scheduleRows.Count} rows)."
                            : "."),
                    TotalDoors = items.Count,
                    Doors = items,
                    Warnings = warnings,
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new CheckFireDoorsResult
                {
                    Success = false,
                    Message = $"Failed to collect door facts: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Check Fire Doors";

        internal static bool IsOnEgressPath(string fromRoom, string toRoom, FamilyInstance door)
        {
            if (ContainsEgressKeyword(fromRoom) || ContainsEgressKeyword(toRoom))
                return true;

            if (door == null)
                return false;

            var comments = door.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                ?.AsString()?.ToLowerInvariant() ?? string.Empty;

            return comments.Contains("эвак") || comments.Contains("egress");
        }

        internal static bool IsBetweenCompartments(string fromRoom, string toRoom)
        {
            var fromEgress = ContainsEgressKeyword(fromRoom) || IsStairwell(fromRoom) || IsVestibule(fromRoom);
            var toEgress = ContainsEgressKeyword(toRoom) || IsStairwell(toRoom) || IsVestibule(toRoom);
            var fromResidential = IsResidentialSpace(fromRoom);
            var toResidential = IsResidentialSpace(toRoom);

            if (fromEgress && toResidential)
                return true;

            if (toEgress && fromResidential)
                return true;

            if (IsStairwell(fromRoom) && ContainsEgressKeyword(toRoom))
                return true;

            if (IsStairwell(toRoom) && ContainsEgressKeyword(fromRoom))
                return true;

            return false;
        }

        internal static bool ContainsEgressKeyword(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var normalized = value.ToLowerInvariant();
            return normalized.Contains("коридор")
                || normalized.Contains("лест")
                || normalized.Contains("эвак")
                || normalized.Contains("corridor")
                || normalized.Contains("stair")
                || normalized.Contains("egress")
                || normalized.Contains("hall");
        }

        private static bool IsStairwell(string roomName)
        {
            if (string.IsNullOrWhiteSpace(roomName))
                return false;

            var normalized = roomName.ToLowerInvariant();
            return normalized.Contains("лестнич")
                || normalized.Contains("stair")
                || normalized.Contains("лк ");
        }

        private static bool IsVestibule(string roomName)
        {
            if (string.IsNullOrWhiteSpace(roomName))
                return false;

            var normalized = roomName.ToLowerInvariant();
            return normalized.Contains("тамбур")
                || normalized.Contains("вестиб")
                || normalized.Contains("vestibule");
        }

        private static bool IsResidentialSpace(string roomName)
        {
            if (string.IsNullOrWhiteSpace(roomName))
                return false;

            var normalized = roomName.ToLowerInvariant();
            return normalized.Contains("квартир")
                || normalized.Contains("жил")
                || normalized.Contains("комнат")
                || normalized.Contains("спальн")
                || normalized.Contains("гостин")
                || normalized.Contains("кухн")
                || normalized.Contains("apartment")
                || normalized.Contains("bedroom")
                || normalized.Contains("living");
        }

        /// <summary>
        /// Reads dedicated fire-rating / EI parameters only (not Yes/No, not notes).
        /// </summary>
        internal static string ReadParameterFireRating(FamilyInstance door)
        {
            foreach (var name in FireRatingParameterNames)
            {
                var value = ReadParameterText(door, name);
                if (!string.IsNullOrWhiteSpace(value) && LooksLikeRatingValue(value))
                    return value;
            }

            var familyName = door.Symbol?.Family?.Name ?? string.Empty;
            var typeName = door.Symbol?.Name ?? string.Empty;
            var combined = $"{familyName} {typeName}".ToLowerInvariant();
            if (combined.Contains("противопожар")
                || System.Text.RegularExpressions.Regex.IsMatch(combined, @"\bei[\s\-]*\d+")
                || combined.Contains("fire"))
            {
                return typeName;
            }

            return string.Empty;
        }

        /// <summary>Legacy alias used by tests / callers expecting combined rating text.</summary>
        internal static string ReadFireRating(FamilyInstance door)
        {
            var rating = ReadParameterFireRating(door);
            if (!string.IsNullOrWhiteSpace(rating))
                return rating;

            return ReadNoteParameters(door);
        }

        internal static bool IsMarkedAsFireDoor(FamilyInstance door, string fireRating)
        {
            return IsMarkedFromParameters(door, fireRating)
                   || FireDoorScheduleNoteReader.LooksLikeFireDoorText(ReadNoteParameters(door));
        }

        internal static bool IsMarkedFromParameters(FamilyInstance door, string fireRating)
        {
            if (FireDoorScheduleNoteReader.LooksLikeFireDoorText(fireRating))
                return true;

            var yesNoParameter = door.LookupParameter("Противопожарная")
                ?? door.Symbol?.LookupParameter("Противопожарная");
            if (yesNoParameter != null && yesNoParameter.HasValue)
            {
                if (yesNoParameter.StorageType == StorageType.Integer)
                    return yesNoParameter.AsInteger() == 1;

                var text = yesNoParameter.AsValueString()?.ToLowerInvariant() ?? string.Empty;
                if (text.Contains("да") || text.Contains("yes") || text == "1")
                    return true;
            }

            return false;
        }

        internal static string ReadNoteParameters(FamilyInstance door)
        {
            foreach (var name in NoteParameterNames)
            {
                var value = ReadParameterText(door, name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            var typeComments = door.Symbol?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)
                ?.AsString();
            if (!string.IsNullOrWhiteSpace(typeComments))
                return typeComments!;

            var instanceComments = door.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                ?.AsString();
            if (!string.IsNullOrWhiteSpace(instanceComments))
                return instanceComments!;

            return string.Empty;
        }

        private static string ReadParameterText(FamilyInstance door, string name)
        {
            var parameter = door.LookupParameter(name) ?? door.Symbol?.LookupParameter(name);
            if (parameter == null || !parameter.HasValue)
                return string.Empty;

            // Skip Yes/No falsely treated as rating text
            if (parameter.StorageType == StorageType.Integer
                && (name.Contains("Противопож", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Противопожарная", StringComparison.OrdinalIgnoreCase)))
            {
                return string.Empty;
            }

            var display = parameter.AsValueString();
            if (!string.IsNullOrWhiteSpace(display))
                return display!;

            if (parameter.StorageType == StorageType.String)
            {
                var raw = parameter.AsString();
                if (!string.IsNullOrWhiteSpace(raw))
                    return raw!;
            }

            return string.Empty;
        }

        private static bool LooksLikeRatingValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var normalized = value.ToLowerInvariant().Trim();
            if (normalized is "нет" or "no" or "0" or "-" or "—")
                return false;

            return FireDoorScheduleNoteReader.LooksLikeFireDoorText(value)
                   || System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\bei\b");
        }

        internal static string ExtractFireRatingSnippet(string note)
        {
            if (string.IsNullOrWhiteSpace(note))
                return string.Empty;

            var match = System.Text.RegularExpressions.Regex.Match(
                note,
                @"EI[\s\-]*\d+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
                return System.Text.RegularExpressions.Regex
                    .Replace(match.Value, @"[\s\-]+", string.Empty)
                    .ToUpperInvariant();

            if (note.IndexOf("противопожар", StringComparison.OrdinalIgnoreCase) >= 0)
                return "противопожарная";

            return note.Length <= 80 ? note : note.Substring(0, 80);
        }

        private static double? GetDoorWidthMm(FamilyInstance door)
        {
            var widthParam = door.get_Parameter(BuiltInParameter.DOOR_WIDTH)
                ?? door.Symbol?.get_Parameter(BuiltInParameter.DOOR_WIDTH)
                ?? door.LookupParameter("Width")
                ?? door.LookupParameter("Ширина");

            if (widthParam == null || !widthParam.HasValue || widthParam.StorageType != StorageType.Double)
                return null;

            return RevitUnitConversion.ToMillimeters(widthParam.AsDouble());
        }
    }
}
