using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Choosing which room tag family type to place.
    ///
    /// The old rule was <c>FirstOrDefault()</c> over every room tag type in the
    /// document. In the Russian template the first one is «Марка помещения» —
    /// name and number, no area — so «поставь марку помещения с квадратурой»
    /// placed twelve name-only tags, and the assistant reported «марки с
    /// названием и площадью уже стоят» over a plan that showed nothing of the
    /// sort (19.08.2026, twice in five minutes).
    ///
    /// Whether a tag *displays* area cannot be read from the placed instance:
    /// the label lives inside the family document, and opening it with
    /// <c>EditFamily</c> is far too heavy to do while tagging. The type name is
    /// what remains — every stock template names the variant («… с площадью»,
    /// "Room Tag With Area"), and offices name their own the same way.
    ///
    /// So the match is by name, and it is allowed to fail: a caller that asked
    /// for area and cannot get it is told, never quietly handed a name-only
    /// tag. Guessing wrong here is what produced the false answer in the first
    /// place.
    /// </summary>
    public static class RoomTagTypes
    {
        /// <summary>Words a tag type carrying room area is named with.</summary>
        private static readonly string[] AreaWords =
        {
            "площад",   // «с площадью», «Площадь», «имя+площадь»
            "квадрат",  // «с квадратурой» — how the request itself is phrased
            "area",     // "Room Tag With Area"
        };

        /// <summary>All room tag types in the document.</summary>
        public static List<ElementType> All(Document doc)
        {
            if (doc == null)
            {
                return new List<ElementType>();
            }

            return new FilteredElementCollector(doc)
                .OfClass(typeof(ElementType))
                .WhereElementIsElementType()
                .Cast<ElementType>()
                .Where(type =>
                    type.Category != null
                    && type.Category.Id.GetIntValue() == (int)BuiltInCategory.OST_RoomTags)
                .ToList();
        }

        /// <summary>
        /// The type a caller explicitly asked for, or null when the id names
        /// something that is not a room tag type.
        /// </summary>
        public static ElementId Requested(Document doc, string tagTypeId)
        {
            if (doc == null || string.IsNullOrEmpty(tagTypeId) || !int.TryParse(tagTypeId, out int id))
            {
                return null;
            }

            var elementId = new ElementId(id);
            var element = doc.GetElement(elementId) as ElementType;

            bool isRoomTagType =
                element != null
                && element.Category != null
                && element.Category.Id.GetIntValue() == (int)BuiltInCategory.OST_RoomTags;

            return isRoomTagType ? elementId : null;
        }

        /// <summary>
        /// Whether this type's name says it carries the room area.
        /// Both halves are checked: offices put the distinction in the type
        /// name («Марка помещения: с площадью»), templates in the family name.
        /// </summary>
        public static bool ShowsArea(ElementType type)
        {
            if (type == null)
            {
                return false;
            }

            string name = ((type.Name ?? string.Empty) + " " + (type.FamilyName ?? string.Empty))
                .ToLowerInvariant();

            return AreaWords.Any(word => name.Contains(word));
        }

        /// <summary>
        /// Pick the type to place.
        /// </summary>
        /// <param name="requireArea">
        /// The caller asked for area on the tag. When nothing in the project
        /// shows it, this returns null and fills <paramref name="error"/> — the
        /// caller must refuse rather than fall back.
        /// </param>
        public static ElementId Pick(
            Document doc,
            string tagTypeId,
            bool requireArea,
            out string error,
            out string chosenName)
        {
            error = null;
            chosenName = null;

            ElementId requested = Requested(doc, tagTypeId);
            if (requested != null)
            {
                // An explicit id wins even when it shows no area: the caller
                // named the type, and second-guessing that would be its own
                // kind of lie.
                chosenName = DescribeType(doc.GetElement(requested) as ElementType);
                return requested;
            }

            var types = All(doc);
            if (types.Count == 0)
            {
                error = "В проекте нет ни одного типа марки помещения. Загрузите семейство марки.";
                return null;
            }

            if (requireArea)
            {
                var withArea = types.FirstOrDefault(ShowsArea);
                if (withArea == null)
                {
                    error =
                        "В проекте нет марки помещения с площадью — есть только: "
                        + string.Join(", ", types.Select(DescribeType).Take(10))
                        + ". Загрузите семейство «Марка помещения с площадью» "
                        + "(Вставить → Загрузить семейство) и повторите.";
                    return null;
                }

                chosenName = DescribeType(withArea);
                return withArea.Id;
            }

            var first = types.First();
            chosenName = DescribeType(first);
            return first.Id;
        }

        private static string DescribeType(ElementType type)
        {
            if (type == null)
            {
                return "(без имени)";
            }

            string family = type.FamilyName ?? string.Empty;
            string name = type.Name ?? string.Empty;

            return string.IsNullOrEmpty(family) ? name : family + ": " + name;
        }
    }
}
