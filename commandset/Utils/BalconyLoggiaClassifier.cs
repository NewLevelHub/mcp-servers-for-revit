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
            Terrace,
            /// <summary>Путь к незадымляемой ЛК (Н1): воздушная зона / галерея — п. 4.2.30.</summary>
            FirePathOutdoor
        }

        public static OutdoorSpaceKind Classify(string roomName, string roomPurpose = "")
        {
            var combined = $"{roomName} {roomPurpose}".Trim();
            if (string.IsNullOrWhiteSpace(combined))
                return OutdoorSpaceKind.Unknown;

            var normalized = combined.ToLowerInvariant();

            // Fire-path outdoor before private summer rooms (REV-50).
            if (IsFirePathOutdoorName(normalized))
                return OutdoorSpaceKind.FirePathOutdoor;

            if (normalized.Contains("лодж") || normalized.Contains("loggia") || normalized.Contains("лоджа"))
                return OutdoorSpaceKind.Loggia;

            if (normalized.Contains("балкон") || normalized.Contains("balcon"))
                return OutdoorSpaceKind.Balcony;

            if (normalized.Contains("террас") || normalized.Contains("terrace")
                || normalized.Contains("веранд") || normalized.Contains("veranda"))
                return OutdoorSpaceKind.Terrace;

            // Summer rooms without explicit kind (1st floor naming variants, REV-50).
            if (normalized.Contains("летнее") || normalized.Contains("летнее помещен")
                || normalized.Contains("летнее помещ") || normalized.Contains("summer room")
                || normalized.Contains("жазғы"))
                return OutdoorSpaceKind.Loggia;

            return OutdoorSpaceKind.Unknown;
        }

        /// <summary>
        /// Воздушная зона / галерея к незадымляемой ЛК — п. 4.2.30 (не квартирная лоджия).
        /// </summary>
        public static bool IsFirePathOutdoor(string roomName, string roomPurpose = "")
        {
            return Classify(roomName, roomPurpose) == OutdoorSpaceKind.FirePathOutdoor;
        }

        private static bool IsFirePathOutdoorName(string normalized)
        {
            if (normalized.Contains("воздушн") || normalized.Contains("air zone")
                || normalized.Contains("ауа аймақ") || normalized.Contains("ауа аймаг"))
                return true;

            // Галерея эвакуации / к незадымляемой ЛК — не «галерея» в общем смысле торгового.
            if ((normalized.Contains("галере") || normalized.Contains("gallery"))
                && (normalized.Contains("незадымл") || normalized.Contains("эвак")
                    || normalized.Contains("лестнич") || normalized.Contains("н1")
                    || normalized.Contains("h1") || normalized.Contains("түтіндет")))
                return true;

            return false;
        }

        public static bool IsBalconyOrLoggia(string roomName, string roomPurpose = "")
        {
            var kind = Classify(roomName, roomPurpose);
            return kind == OutdoorSpaceKind.Balcony
                || kind == OutdoorSpaceKind.Loggia
                || kind == OutdoorSpaceKind.Terrace;
        }

        /// <summary>Private summer room or fire-path outdoor (воздушная зона).</summary>
        public static bool IsOutdoorSpaceForMinDimensions(string roomName, string roomPurpose = "")
        {
            var kind = Classify(roomName, roomPurpose);
            return kind == OutdoorSpaceKind.Balcony
                || kind == OutdoorSpaceKind.Loggia
                || kind == OutdoorSpaceKind.Terrace
                || kind == OutdoorSpaceKind.FirePathOutdoor;
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
                OutdoorSpaceKind.FirePathOutdoor => "воздушная зона / путь к Н1",
                _ => "неизвестно"
            };
        }
    }
}
