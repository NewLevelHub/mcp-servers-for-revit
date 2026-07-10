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
    /// Normative rules are read from repo/normatives PDFs on the MCP server (REV-29).
    /// </summary>
    public class CheckFireDoorsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
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
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;
                var doorInstances = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
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
                    var fireRating = ReadFireRating(door);

                    items.Add(new DoorFireFacts
                    {
                        Id = door.Id.GetValue(),
                        UniqueId = door.UniqueId,
                        Mark = door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? string.Empty,
                        Family = door.Symbol?.Family?.Name ?? string.Empty,
                        Type = door.Symbol?.Name ?? string.Empty,
                        Level = levelName,
                        FromRoom = fromRoom,
                        ToRoom = toRoom,
                        OpeningWidthMm = GetDoorWidthMm(door),
                        IsOnEgressPath = IsOnEgressPath(fromRoom, toRoom, door),
                        IsMarkedAsFireDoor = IsMarkedAsFireDoor(door, fireRating),
                        CurrentFireRating = fireRating
                    });
                }

                ResultInfo = new CheckFireDoorsResult
                {
                    Success = true,
                    Message = $"Collected fire-door facts for {items.Count} doors.",
                    TotalDoors = items.Count,
                    Doors = items
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

        internal static bool IsOnEgressPath(string fromRoom, string toRoom, FamilyInstance? door)
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

        internal static string ReadFireRating(FamilyInstance door)
        {
            var candidates = new[]
            {
                "ADSK_Предел огнестойкости экземпляра",
                "ADSK_Предел огнестойкости",
                "Fire Rating",
                "Противопожарность",
                "Противопожарная",
                "Огнестойкость",
                "EI",
                "Предел огнестойкости",
                "BI_примечание",
                "ADSK_Примечание"
            };

            foreach (var name in candidates)
            {
                var parameter = door.LookupParameter(name)
                    ?? door.Symbol?.LookupParameter(name);
                if (parameter == null || !parameter.HasValue)
                    continue;

                var display = parameter.AsValueString();
                if (!string.IsNullOrWhiteSpace(display))
                    return display;

                if (parameter.StorageType == StorageType.String)
                {
                    var raw = parameter.AsString();
                    if (!string.IsNullOrWhiteSpace(raw))
                        return raw;
                }
            }

            var familyName = door.Symbol?.Family?.Name ?? string.Empty;
            var typeName = door.Symbol?.Name ?? string.Empty;
            var combined = $"{familyName} {typeName}".ToLowerInvariant();
            if (combined.Contains("противопожар") || combined.Contains("ei") || combined.Contains("fire"))
                return typeName;

            return string.Empty;
        }

        internal static bool IsMarkedAsFireDoor(FamilyInstance door, string fireRating)
        {
            if (!string.IsNullOrWhiteSpace(fireRating))
            {
                var normalized = fireRating.ToLowerInvariant();
                if (normalized.Contains("ei") || normalized.Contains("противопожар") || normalized.Contains("fire"))
                    return true;
            }

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
