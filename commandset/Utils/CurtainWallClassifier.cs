using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    ///     Identifies curtain wall systems (витражи) for schedule export and validation.
    ///     A витраж is the curtain WALL itself (WallKind.Curtain), never its glazing
    ///     panels or mullions — РД counts one position per wall system.
    /// </summary>
    public static class CurtainWallClassifier
    {
        public static bool IsCurtainWall(Element element)
        {
            return element is Wall wall && wall.WallType?.Kind == WallKind.Curtain;
        }

        /// <summary>
        ///     Optional narrowing by type name, e.g. '(витражи)' matches types like
        ///     '(витражи)1200х2900h'. Empty filter accepts every curtain wall.
        /// </summary>
        public static bool MatchesTypeFilter(Wall wall, string typeNameFilter)
        {
            if (string.IsNullOrWhiteSpace(typeNameFilter))
                return true;

            var typeName = wall.WallType?.Name ?? string.Empty;
            return typeName.IndexOf(typeNameFilter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
