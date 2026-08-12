namespace RevitMCPCommandSet.Utils;

/// <summary>
///     Dismisses Revit warnings during a transaction and records what was dismissed.
///     <para>
///     Without a preprocessor Revit raises the warning as a modal dialog on commit. Inside an
///     ExternalEvent nobody can click it, so the command sits there for tens of seconds
///     (measured: 41 s for one wall batch) and a "Cancel" click rolls the whole batch back —
///     the elements silently never appear. Swallowing warnings blindly is just as bad, so the
///     descriptions come back to the caller and end up in the tool response.
///     </para>
/// </summary>
public sealed class RecordingWarningsPreprocessor : IFailuresPreprocessor
{
    private readonly Dictionary<string, int> _dismissed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Warning text → how many times it was dismissed in this transaction.</summary>
    public IReadOnlyDictionary<string, int> Dismissed => _dismissed;

    /// <summary>True when a warning fired — the caller should surface this, not report a clean run.</summary>
    public bool HasDismissals => _dismissed.Count > 0;

    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        if (failuresAccessor == null)
            return FailureProcessingResult.Continue;

        var messages = failuresAccessor.GetFailureMessages();
        if (messages == null || messages.Count == 0)
            return FailureProcessingResult.Continue;

        foreach (var failure in messages)
        {
            if (failure == null)
                continue;

            if (failure.GetSeverity() == FailureSeverity.Warning)
            {
                Record(SafeDescription(failure));
                failuresAccessor.DeleteWarning(failure);
                continue;
            }

            // Errors are left alone deliberately: resolving them silently is how a traced wall
            // ends up somewhere other than the DWG says. Let the transaction fail loudly.
        }

        return FailureProcessingResult.Continue;
    }

    /// <summary>Human-readable lines for the tool response, most frequent first.</summary>
    public List<string> ToWarningLines(string prefix)
    {
        return _dismissed
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Value > 1
                ? $"{prefix}: {kv.Key} (×{kv.Value})"
                : $"{prefix}: {kv.Key}")
            .ToList();
    }

    private void Record(string description)
    {
        _dismissed.TryGetValue(description, out var count);
        _dismissed[description] = count + 1;
    }

    private static string SafeDescription(FailureMessageAccessor failure)
    {
        try
        {
            var text = failure.GetDescriptionText();
            return string.IsNullOrWhiteSpace(text) ? failure.GetFailureDefinitionId().Guid.ToString() : text.Trim();
        }
        catch
        {
            return "unnamed Revit warning";
        }
    }
}
