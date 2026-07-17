using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Rooms in scope for living-room depth norms (СП РК 3.02-101 п. 4.4.10.22):
    /// жилые комнаты — спальни, гостиные, детские, кабинеты и т.п.
    /// Not corridors, stairs, PON, summer rooms, kitchens, baths (REV-50).
    /// </summary>
    public static class LivingRoomDepthClassifier
    {
        /// <summary>Tokens that mark a living room (А.8 / п. 4.4.10.22).</summary>
        private static readonly string[] LivingTokens =
        {
            "жилая", "жилое", "жилой", "жилую", "жил комнат",
            "спальн", "гостин", "детск", "кабинет", "библиотек",
            "столов", "игров", "общая комната", "living", "bedroom",
            "тұрғын", "жатын", "қонақ"
        };

        /// <summary>Spaces that must never receive the living-room depth max.</summary>
        private static readonly string[] ExcludedTokens =
        {
            "коридор", "corridor", "дәліз",
            "лестниц", "лестнич", "stair", "баспалдақ",
            "пон", "помещени обществен", "общественн назначен",
            "тамбур", "tambour", "vestibule",
            "лодж", "балкон", "balcony", "loggia", "террас", "веранд",
            "сануз", "ванн", "туалет", "душевая", "wc", "дәретхана",
            "лифт", "холл", "hall",
            "кладов", "гардероб", "техн", "площадк",
            "нежил", "мәлімет"
        };

        /// <summary>
        /// True when the room is a living room for depth check applicability.
        /// </summary>
        public static bool IsLivingRoomForDepth(string roomName, string roomPurpose = "")
        {
            var text = $"{roomName ?? string.Empty} {roomPurpose ?? string.Empty}"
                .ToLowerInvariant()
                .Trim();
            if (string.IsNullOrEmpty(text))
                return false;

            // «нежилое» before any «жил*» token.
            if (text.Contains("нежил"))
                return false;

            foreach (var token in ExcludedTokens)
            {
                if (text.Contains(token))
                    return false;
            }

            // Кухня alone — not a living room; кухня-гостиная keeps «гостин».
            if (text.Contains("кухн") && !text.Contains("гостин"))
                return false;

            foreach (var token in LivingTokens)
            {
                if (text.Contains(token))
                    return true;
            }

            return false;
        }

        public static bool IsLivingRoomForDepth(Room room)
        {
            if (room == null)
                return false;

            var roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
            var purpose = CorridorClassifier.ReadRoomPurpose(room);
            return IsLivingRoomForDepth(roomName, purpose);
        }

        /// <summary>
        /// Filters like «жилая» / «living» mean semantic living scope, not a name substring.
        /// </summary>
        public static bool IsLivingScopeAlias(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return false;

            var normalized = filter.Trim().ToLowerInvariant();
            return normalized is "жилая" or "жилое" or "жилые" or "жилых"
                or "living" or "living room" or "жилая комната" or "жилые комнаты"
                or "тұрғын" or "тұрғын бөлме";
        }
    }
}
