using System;
using System.Collections.Generic;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Parameters <see cref="NormCheckDefaults.EnrichArgs"/> may inject per tool.
    /// Must be declared in <see cref="ToolCatalog"/> unless listed in
    /// <see cref="InternalEnrichmentKeys"/> (REV-117 guardian).
    /// </summary>
    public static class NormCheckSchemaKeys
    {
        public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EnrichmentKeysByTool =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["check_evacuation_width"] = new[]
                {
                    "minWidthMm", "filterByActiveView", "levelId", "viewId", "levelName"
                },
                ["check_room_depth"] = new[]
                {
                    "maxDepthMm", "roomScope", "filterByActiveView", "levelId", "viewId", "levelName"
                },
                ["check_min_dimensions"] = new[]
                {
                    "minFirePathOutdoorWidthMm", "minFirePierToOpeningMm", "minBalconyWidthMm",
                    "minLoggiaWidthMm", "minLoggiaDepthMm", "minFirePierBetweenOpeningsMm",
                    "filterByActiveView", "levelId", "viewId", "levelName"
                },
                ["check_fire_doors"] = new[]
                {
                    "filterByActiveView", "levelId", "viewId", "levelName"
                },
                ["export_room_data"] = new[]
                {
                    "filterByActiveView"
                },
            };

        /// <summary>Runtime-only enrichment — not exposed to the model in tool schemas.</summary>
        public static readonly HashSet<string> InternalEnrichmentKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "source", "catalogUsed", "catalogMissing"
            };
    }
}
