using Autodesk.Revit.DB.Architecture;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Heuristic corridor detection from room names and purpose parameters (REV-31 / REV-33).
    /// </summary>
    public static class CorridorClassifier
    {
        private static readonly string[] PurposeParameterNames =
        {
            "ADSK_Назначение",
            "BI_назначение",
            "Назначение",
            "Room Usage",
            "Имя помещения",
        };

        public static string ReadRoomPurpose(Room room)
        {
            if (room == null)
                return string.Empty;

            foreach (var parameterName in PurposeParameterNames)
            {
                var parameter = room.LookupParameter(parameterName);
                if (parameter == null || !parameter.HasValue)
                    continue;

                var value = parameter.AsString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return string.Empty;
        }

        public static bool IsCorridor(string roomName)
        {
            return MatchesCorridorKeywords(roomName);
        }

        /// <summary>
        /// Broader filter for evacuation-related circulation spaces.
        /// </summary>
        public static bool IsEvacuationCorridor(string roomName, string roomPurpose = "")
        {
            var combined = $"{roomName} {roomPurpose}".Trim();
            if (string.IsNullOrWhiteSpace(combined))
                return false;

            if (MatchesCorridorKeywords(combined))
                return true;

            var normalized = combined.ToLowerInvariant();
            return normalized.Contains("эвак")
                || normalized.Contains("дәліз")
                || normalized.Contains("вестиб")
                || normalized.Contains("лестнич")
                || normalized.Contains("тамбур")
                || normalized.Contains("лифтов")
                || normalized.Contains("холл")
                || normalized.Contains("площадк")
                || normalized.Contains("переход");
        }

        private static bool MatchesCorridorKeywords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var normalized = text.ToLowerInvariant();
            return normalized.Contains("коридор")
                || normalized.Contains("corridor")
                || normalized.Contains("hall");
        }
    }
}
