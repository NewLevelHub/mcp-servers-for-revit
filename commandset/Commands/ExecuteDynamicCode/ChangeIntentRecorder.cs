namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode;

/// <summary>
///     REV-175: the "journal of intent" the ticket asks for — a before/after snapshot of which
///     elements exist, by category, so the caller can see what a sandboxed snippet created or
///     deleted without needing the change to ever commit.
///     <para>
///     Deliberately NOT built on <c>Application.DocumentChanged</c>. Verified live against the
///     running plugin (REV-175, 24.08.2026): a trial that ends in <c>Transaction.RollBack()</c>
///     does fire the event once, but with an empty change set (added=0/deleted=0/modified=0) —
///     Revit only reports real content on a successful commit, which a trial by definition never
///     does. <c>SubTransaction.Commit()</c> doesn't fire it at all either. So the only signal
///     that works for a run that must stay invisible to the document is a plain element-id diff,
///     read straight from the collector while the transaction is still open (reads inside an
///     open transaction already reflect its pending state).
///     </para>
///     <para>
///     Only creation and deletion are tracked — not in-place parameter edits to elements that
///     are neither created nor deleted. Detecting those would need a per-element fingerprint,
///     and that was measured live on the currently open model (~50k elements): a category-only
///     pass costs well under a second, but a per-parameter value hash — even without the
///     formatting <c>AsValueString()</c> does — was still running past 20s and had to be
///     abandoned. Not viable as something every trial run pays for.
///     </para>
/// </summary>
public sealed class ChangeIntentRecorder
{
    private readonly Document _doc;
    private readonly Dictionary<ElementId, string> _before;

    public ChangeIntentRecorder(Document doc)
    {
        _doc = doc;
        _before = Snapshot(doc);
    }

    /// <summary>Call once, after the snippet has run, while the transaction is still open.</summary>
    public ChangeIntent Diff()
    {
        var after = Snapshot(_doc);

        var created = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in after)
            if (!_before.ContainsKey(kv.Key))
                Bump(created, kv.Value);

        var deleted = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in _before)
            if (!after.ContainsKey(kv.Key))
                Bump(deleted, kv.Value);

        return new ChangeIntent(created, deleted);
    }

    private static Dictionary<ElementId, string> Snapshot(Document doc)
    {
        var map = new Dictionary<ElementId, string>();
        foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            map[element.Id] = DescribeCategory(element);
        return map;
    }

    private static string DescribeCategory(Element element)
    {
        return element?.Category?.Name ?? element?.GetType().Name ?? "неизвестная категория";
    }

    private static void Bump(Dictionary<string, int> bucket, string category)
    {
        bucket.TryGetValue(category, out var count);
        bucket[category] = count + 1;
    }
}

/// <summary>Result of <see cref="ChangeIntentRecorder.Diff" />: what a snippet created/deleted, by category.</summary>
public sealed class ChangeIntent
{
    public static readonly ChangeIntent Empty = new(
        new Dictionary<string, int>(), new Dictionary<string, int>());

    private readonly Dictionary<string, int> _created;
    private readonly Dictionary<string, int> _deleted;

    internal ChangeIntent(Dictionary<string, int> created, Dictionary<string, int> deleted)
    {
        _created = created;
        _deleted = deleted;
    }

    /// <summary>Distinct elements created + deleted. Does NOT include in-place edits — see class remarks.</summary>
    public int TouchedCount => _created.Values.Sum() + _deleted.Values.Sum();

    /// <summary>Human-readable (Russian) summary for the caller to show an architect.</summary>
    public string BuildReport()
    {
        if (_created.Count == 0 && _deleted.Count == 0)
            return "не создал и не удалил элементов (проба не отслеживает точечные правки " +
                   "параметров у уже существующих элементов).";

        var parts = new List<string>();
        AppendPart(parts, "создаст", _created);
        AppendPart(parts, "удалит", _deleted);
        return string.Join("; ", parts) + ".";
    }

    private static void AppendPart(List<string> parts, string verb, Dictionary<string, int> bucket)
    {
        if (bucket.Count == 0)
            return;

        var byCategory = string.Join(", ", bucket
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key} — {kv.Value}"));
        parts.Add($"{verb} {bucket.Values.Sum()}: {byCategory}");
    }
}
