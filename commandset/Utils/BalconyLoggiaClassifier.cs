using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Heuristic balcony/loggia detection from room names and purpose (REV-35).
    /// </summary>
    public static class BalconyLoggiaClassifier
    {
        public enum OutdoorSpaceKind
        {
            Unknown,
            Balcony,
            Loggia,
            Terrace
        }

        public static OutdoorSpaceKind Classify(string roomName, string roomPurpose = "")
        {
            var combined = $"{roomName} {roomPurpose}".Trim();
            if (string.IsNullOrWhiteSpace(combined))
                return OutdoorSpaceKind.Unknown;

            var normalized = combined.ToLowerInvariant();

            if (normalized.Contains("лодж") || normalized.Contains("loggia"))
                return OutdoorSpaceKind.Loggia;

            if (normalized.Contains("балкон") || normalized.Contains("balcon"))
                return OutdoorSpaceKind.Balcony;

            if (normalized.Contains("террас") || normalized.Contains("terrace"))
                return OutdoorSpaceKind.Terrace;

            return OutdoorSpaceKind.Unknown;
        }

        public static bool IsBalconyOrLoggia(string roomName, string roomPurpose = "")
        {
            var kind = Classify(roomName, roomPurpose);
            return kind == OutdoorSpaceKind.Balcony
                || kind == OutdoorSpaceKind.Loggia
                || kind == OutdoorSpaceKind.Terrace;
        }

        public static bool IsBalconyOrLoggia(Room room)
        {
            if (room == null)
                return false;

            var roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
            var purpose = CorridorClassifier.ReadRoomPurpose(room);
            return IsBalconyOrLoggia(roomName, purpose);
        }

        public static string KindLabel(OutdoorSpaceKind kind)
        {
            return kind switch
            {
                OutdoorSpaceKind.Balcony => "балкон",
                OutdoorSpaceKind.Loggia => "лоджия",
                OutdoorSpaceKind.Terrace => "терраса",
                _ => "неизвестно"
            };
        }
    }
}
