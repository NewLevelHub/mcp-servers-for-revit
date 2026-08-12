using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils.Detailing;

/// <summary>
///     Finding — and when the template has nothing suitable, making — the filled region type that
///     hatches a detail correctly.
///     <para>
///     Same reasoning as ensure_wall_type (REV-153/154): a node drawn with whatever solid fill
///     happened to be in the template is a wrong drawing, not a tolerable one. Duplicating a type
///     and pointing it at the right hatch is exactly what a person does by hand.
///     </para>
///     Callers own the transaction.
/// </summary>
public static class FilledRegionTypes
{
    public static List<FilledRegionType> All(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(FilledRegionType))
            .Cast<FilledRegionType>()
            .ToList();
    }

    public static FilledRegionType FindByName(Document doc, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var wanted = name.Trim();
        var types = All(doc);

        return types.FirstOrDefault(type => type.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
               ?? types.FirstOrDefault(type => type.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    ///     Drafting patterns are what details hatch with; a model pattern of the same name would
    ///     scale wrongly on a 1:10 node, so exact-name drafting wins over a partial match.
    /// </summary>
    public static FillPatternElement FindPattern(Document doc, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var wanted = name.Trim();
        var patterns = new FilteredElementCollector(doc)
            .OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .ToList();

        bool IsDrafting(FillPatternElement pattern) =>
            pattern.GetFillPattern()?.Target == FillPatternTarget.Drafting;

        return patterns.FirstOrDefault(p => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) && IsDrafting(p))
               ?? patterns.FirstOrDefault(p => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
               ?? patterns.FirstOrDefault(p => p.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 && IsDrafting(p))
               ?? patterns.FirstOrDefault(p => p.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>An existing type already drawing with this pattern, preferring non-masking ones.</summary>
    public static FilledRegionType FindByPattern(Document doc, ElementId patternId)
    {
        if (patternId == null || patternId == ElementId.InvalidElementId)
            return null;

        var matches = All(doc)
            .Where(type => type.ForegroundPatternId == patternId)
            .ToList();

        return matches.FirstOrDefault(type => !type.IsMasking) ?? matches.FirstOrDefault();
    }

    /// <summary>
    ///     Returns a filled region type drawing with <paramref name="pattern" />, duplicating an
    ///     existing type when the project has none. Requires an open transaction.
    /// </summary>
    public static FilledRegionType EnsureForPattern(
        Document doc,
        FillPatternElement pattern,
        Color foregroundColor,
        out bool created)
    {
        created = false;
        if (pattern == null)
            return null;

        var existing = FindByPattern(doc, pattern.Id);
        if (existing != null)
            return existing;

        var source = All(doc).FirstOrDefault(type => !type.IsMasking) ?? All(doc).FirstOrDefault();
        if (source == null)
            return null;

        var name = UniqueTypeName(doc, $"MCP {pattern.Name}");
        if (source.Duplicate(name) is not FilledRegionType duplicate)
            return null;

        duplicate.ForegroundPatternId = pattern.Id;
        duplicate.IsMasking = false;

        if (foregroundColor != null && foregroundColor.IsValid)
            duplicate.ForegroundPatternColor = foregroundColor;

        created = true;
        return duplicate;
    }

    public static List<string> ListTypeNames(Document doc, int max = 40)
    {
        return All(doc)
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    public static List<string> ListPatternNames(Document doc, int max = 60)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .Where(pattern => pattern.GetFillPattern()?.Target == FillPatternTarget.Drafting)
            .Select(pattern => pattern.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    private static string UniqueTypeName(Document doc, string desired)
    {
        var taken = new HashSet<string>(All(doc).Select(type => type.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(desired))
            return desired;

        for (var i = 2; i < 100; i++)
        {
            var candidate = $"{desired} ({i})";
            if (!taken.Contains(candidate))
                return candidate;
        }

        return $"{desired} {Guid.NewGuid():N}".Substring(0, 60);
    }
}
