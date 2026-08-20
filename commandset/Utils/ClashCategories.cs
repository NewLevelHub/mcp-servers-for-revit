using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Which categories are worth intersecting with which, by default (REV-167).
    /// </summary>
    /// <remarks>
    /// A clash run over «everything against everything» is both slow and useless: the
    /// architect gets furniture inside floors and finishes inside walls, and stops
    /// reading. The default pairs are the ones a ГАП actually argues about with the
    /// смежники — our walls, floors, ceilings, roofs and openings against their
    /// structure and their пучок труб/воздуховодов/лотков.
    ///
    /// Both sides are overridable per call, so a narrower question («только балки в
    /// проёмах») costs one argument rather than a new tool.
    /// </remarks>
    public static class ClashCategories
    {
        /// <summary>Our side: the architectural fabric the смежник runs into.</summary>
        public static readonly IReadOnlyList<BuiltInCategory> DefaultHost = new[]
        {
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_Ceilings,
            BuiltInCategory.OST_Roofs,
            // Проёмы. A door or window carries the leaf and the frame as solids, so a
            // beam dropping into the opening shows up as an intersection with them —
            // this is the «балка режет проём» case the epic was written for.
            BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_Windows,
            BuiltInCategory.OST_Stairs,
        };

        /// <summary>Their side: structure and the MEP runs that eat clear height.</summary>
        public static readonly IReadOnlyList<BuiltInCategory> DefaultLink = new[]
        {
            BuiltInCategory.OST_StructuralFraming,
            BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_Columns,
            BuiltInCategory.OST_StructuralFoundation,
            BuiltInCategory.OST_DuctCurves,
            BuiltInCategory.OST_FlexDuctCurves,
            BuiltInCategory.OST_DuctFitting,
            BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_FlexPipeCurves,
            BuiltInCategory.OST_PipeFitting,
            BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_Conduit,
            BuiltInCategory.OST_MechanicalEquipment,
        };

        /// <summary>
        /// Turns the names a caller passed into categories of <paramref name="doc"/>,
        /// falling back to <paramref name="fallback"/> when nothing was asked for.
        /// </summary>
        /// <remarks>
        /// A name that resolves to nothing is reported in <paramref name="unresolved"/>
        /// rather than dropped: silently scanning fewer categories than asked for reads
        /// as «коллизий нет», which is the worst possible way to be wrong here.
        /// </remarks>
        public static List<BuiltInCategory> Resolve(
            Document doc,
            IEnumerable<string> names,
            IReadOnlyList<BuiltInCategory> fallback,
            out List<string> unresolved)
        {
            unresolved = new List<string>();

            var wanted = (names ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            if (wanted.Count == 0)
                return fallback.ToList();

            var resolved = new List<BuiltInCategory>();
            foreach (var name in wanted)
            {
                var category = CategoryResolver.Find(doc, name);
                if (category == null)
                {
                    unresolved.Add(name);
                    continue;
                }

                var builtIn = (BuiltInCategory)category.Id.GetValue();
                if (!resolved.Contains(builtIn))
                    resolved.Add(builtIn);
            }

            return resolved;
        }

        /// <summary>Category names as this document spells them, for the report header.</summary>
        public static List<string> Describe(Document doc, IEnumerable<BuiltInCategory> categories)
        {
            var names = new List<string>();

            foreach (var builtIn in categories ?? Enumerable.Empty<BuiltInCategory>())
            {
                string name = null;
                try
                {
                    name = Category.GetCategory(doc, builtIn)?.Name;
                }
                catch
                {
                    // A category the document does not carry at all: report the enum
                    // name rather than nothing, so the caller sees what was asked for.
                }

                names.Add(string.IsNullOrWhiteSpace(name) ? builtIn.ToString() : name);
            }

            return names;
        }
    }
}
