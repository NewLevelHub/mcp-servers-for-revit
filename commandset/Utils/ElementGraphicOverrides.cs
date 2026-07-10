using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Shared view overrides for normative highlight (rooms, tags, walls, etc.).
    /// </summary>
    public static class ElementGraphicOverrides
    {
        public static OverrideGraphicSettings CreateSolidColorOverrides(Document doc, Color color)
        {
            var overrides = new OverrideGraphicSettings();
            overrides.SetProjectionLineColor(color);
            overrides.SetCutLineColor(color);
            overrides.SetSurfaceForegroundPatternColor(color);
            overrides.SetSurfaceBackgroundPatternColor(color);
            overrides.SetCutForegroundPatternColor(color);
            overrides.SetCutBackgroundPatternColor(color);

            var solidPattern = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(pattern => pattern.GetFillPattern().IsSolidFill);

            if (solidPattern != null)
            {
                overrides.SetSurfaceForegroundPatternId(solidPattern.Id);
                overrides.SetSurfaceForegroundPatternVisible(true);
                overrides.SetSurfaceBackgroundPatternId(solidPattern.Id);
                overrides.SetSurfaceBackgroundPatternVisible(true);
                overrides.SetCutForegroundPatternId(solidPattern.Id);
                overrides.SetCutForegroundPatternVisible(true);
                overrides.SetCutBackgroundPatternId(solidPattern.Id);
                overrides.SetCutBackgroundPatternVisible(true);
            }

            return overrides;
        }

        public static IEnumerable<ElementId> FindRoomTagIds(Document doc, View view, ElementId roomId)
        {
            if (view == null || roomId == ElementId.InvalidElementId)
            {
                return Enumerable.Empty<ElementId>();
            }

            return new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_RoomTags)
                .WhereElementIsNotElementType()
                .Cast<RoomTag>()
                .Where(tag => tag.Room != null && tag.Room.Id == roomId)
                .Select(tag => tag.Id);
        }

        public static int ApplyToView(View view, Document doc, IEnumerable<ElementId> elementIds, int[] rgb)
        {
            if (rgb == null || rgb.Length < 3)
            {
                rgb = new[] { 255, 0, 0 };
            }

            var color = new Color(
                (byte)Math.Max(0, Math.Min(255, rgb[0])),
                (byte)Math.Max(0, Math.Min(255, rgb[1])),
                (byte)Math.Max(0, Math.Min(255, rgb[2])));

            var overrides = CreateSolidColorOverrides(doc, color);
            int count = 0;
            foreach (var id in elementIds)
            {
                view.SetElementOverrides(id, overrides);
                count++;
            }

            return count;
        }

        /// <summary>
        /// Highlight rooms and their tags in the active view (red solid fill + red tag text/lines).
        /// </summary>
        public static int HighlightRoomsAndTags(
            View view,
            Document doc,
            IEnumerable<ElementId> roomIds,
            int[] rgb)
        {
            var targetIds = new List<ElementId>();
            foreach (var roomId in roomIds)
            {
                targetIds.Add(roomId);
                targetIds.AddRange(FindRoomTagIds(doc, view, roomId));
            }

            return ApplyToView(view, doc, targetIds, rgb);
        }
    }
}
