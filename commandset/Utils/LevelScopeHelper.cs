using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Match rooms to active floor / level — view name «2 этаж» often ≠ Level.Name in the model.
    /// </summary>
    public static class LevelScopeHelper
    {
        public sealed class Scope
        {
            public string LevelName { get; set; } = string.Empty;
            public long? LevelId { get; set; }
            public long? ViewId { get; set; }
            public bool FilterByActiveView { get; set; }
            public HashSet<long> RoomIdsOnView { get; set; }
        }

        public static Scope BuildScope(
            Document doc,
            View activeView,
            string levelName,
            long? levelId,
            long? viewId,
            bool filterByActiveView)
        {
            var effectiveViewId = viewId;
            if (!effectiveViewId.HasValue && filterByActiveView && activeView != null)
                effectiveViewId = activeView.Id.GetValue();

            HashSet<long> onView = null;
            if (effectiveViewId.HasValue && effectiveViewId.Value > 0)
            {
                onView = new FilteredElementCollector(doc, ElementIdExtensions.FromLong(effectiveViewId.Value))
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .ToElementIds()
                    .Select(id => id.GetValue())
                    .ToHashSet();
            }

            long? effectiveLevelId = levelId;
            string effectiveLevelName = levelName ?? string.Empty;

            if (activeView is ViewPlan plan && plan.GenLevel != null)
            {
                if (!effectiveLevelId.HasValue)
                    effectiveLevelId = plan.GenLevel.Id.GetValue();
                if (string.IsNullOrWhiteSpace(effectiveLevelName))
                    effectiveLevelName = plan.GenLevel.Name ?? string.Empty;
            }

            return new Scope
            {
                LevelName = effectiveLevelName,
                LevelId = effectiveLevelId,
                ViewId = effectiveViewId,
                FilterByActiveView = filterByActiveView || effectiveViewId.HasValue,
                RoomIdsOnView = onView
            };
        }

        public static bool RoomInScope(Room room, Scope scope)
        {
            if (room == null || scope == null)
                return false;

            if (scope.RoomIdsOnView != null && scope.RoomIdsOnView.Count > 0)
                return scope.RoomIdsOnView.Contains(room.Id.GetValue());

            var roomLevelId = room.Level?.Id.GetValue();
            if (scope.LevelId.HasValue && roomLevelId.HasValue)
                return roomLevelId.Value == scope.LevelId.Value;

            if (!string.IsNullOrWhiteSpace(scope.LevelName))
            {
                var roomLevelName = room.Level?.Name ?? string.Empty;
                return LevelNamesMatch(roomLevelName, scope.LevelName);
            }

            return true;
        }

        public static bool LevelNamesMatch(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return false;
            if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;

            var ka = ExtractFloorKey(a);
            var kb = ExtractFloorKey(b);
            return ka != null && kb != null && ka == kb;
        }

        /// <summary>Extract comparable floor key: "2", "-1", "tech" etc.</summary>
        public static string ExtractFloorKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var n = name.Trim().ToLowerInvariant();
            if (n.Contains("тех") || n.Contains("tech"))
                return "tech";
            if (n.Contains("кровл") || n.Contains("roof"))
                return "roof";

            var m = Regex.Match(n, @"-?\d+");
            if (m.Success)
                return m.Value;

            return null;
        }
    }
}
