using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services;

/// <summary>
/// Writes many parameters across many elements in one ExternalEvent and one
/// transaction. Marking three doors used to be three round trips — six when the
/// first spelling missed — because <see cref="SetElementParameterEventHandler"/>
/// takes exactly one element and one parameter.
///
/// Name lookup, the Russian/English alias table and the "did you mean" text all
/// come from <see cref="ElementParameterHelper"/>; this handler adds no second
/// dictionary of its own.
/// </summary>
public class SetElementsParametersEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    public List<ElementParameterEdit> Edits { get; set; } = new();
    public SetElementsParametersResult Result { get; private set; } = new();
    public bool TaskCompleted { get; private set; }
    private readonly ManualResetEvent _resetEvent = new(false);

    /// <summary>Reset wait state before ExternalEvent.Raise.</summary>
    public void Prepare()
    {
        TaskCompleted = false;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        var results = new List<ParameterWriteResult>();
        try
        {
            var doc = app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("No active Revit document.");

            if (Edits == null || Edits.Count == 0)
                throw new ArgumentException("edits must contain at least one element.");

            using (var transaction = new Transaction(doc, "Set Elements Parameters"))
            {
                transaction.Start();

                foreach (var edit in Edits)
                    WriteOneElement(doc, edit, results);

                // A failure on one parameter must not discard the writes that
                // worked (Д1). Roll back only when nothing landed at all.
                if (results.Any(r => r.Success))
                    transaction.Commit();
                else
                    transaction.RollBack();
            }

            var updated = results.Count(r => r.Success);
            var failed = results.Count - updated;

            Result = new SetElementsParametersResult
            {
                // Zero writes is a refusal, not a success — toolOutcome turns this
                // into isError so the model stops and reports instead of guessing.
                Success = updated > 0,
                UpdatedCount = updated,
                FailedCount = failed,
                Message = BuildMessage(updated, failed, results),
                Results = results
            };
        }
        catch (Exception ex)
        {
            Result = new SetElementsParametersResult
            {
                Success = false,
                Message = ex.Message,
                UpdatedCount = 0,
                FailedCount = results.Count,
                Results = results
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    private static void WriteOneElement(
        Document doc,
        ElementParameterEdit edit,
        List<ParameterWriteResult> results)
    {
        if (edit?.Parameters == null || edit.Parameters.Count == 0)
            return;

        var element = doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(edit.ElementId));
        if (element == null)
        {
            // One entry per requested write, so updatedCount + failedCount always
            // equals what the caller asked for.
            foreach (var name in edit.Parameters.Keys)
            {
                results.Add(new ParameterWriteResult
                {
                    Success = false,
                    ElementId = edit.ElementId,
                    RequestedName = name,
                    Message = $"Element with id {edit.ElementId} was not found."
                });
            }
            return;
        }

        foreach (var pair in edit.Parameters)
        {
            results.Add(WriteOneParameter(doc, element, edit.ElementId, pair.Key, pair.Value));
        }
    }

    private static ParameterWriteResult WriteOneParameter(
        Document doc,
        Element element,
        long elementId,
        string requestedName,
        object? value)
    {
        try
        {
            var parameter = ElementParameterHelper.FindParameter(element, requestedName)
                ?? throw new ArgumentException(
                    ElementParameterHelper.DescribeMissingParameter(element, requestedName));

            ElementParameterHelper.SetParameterValue(parameter, value, doc);

            return new ParameterWriteResult
            {
                Success = true,
                Message = "Parameter updated successfully.",
                ElementId = elementId,
                RequestedName = requestedName,
                ParameterName = parameter.Definition.Name,
                NewDisplayValue = parameter.AsValueString() ?? string.Empty
            };
        }
        catch (Exception ex)
        {
            return new ParameterWriteResult
            {
                Success = false,
                Message = ex.Message,
                ElementId = elementId,
                RequestedName = requestedName
            };
        }
    }

    /// <summary>
    /// States the count first and then the distinct reasons — a partial result the
    /// model can act on without reading every entry of results[].
    /// </summary>
    private static string BuildMessage(int updated, int failed, List<ParameterWriteResult> results)
    {
        if (failed == 0)
            return $"Updated {updated} parameter(s).";

        var reasons = results
            .Where(r => !r.Success && !string.IsNullOrWhiteSpace(r.Message))
            .Select(r => r.Message)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        var head = updated > 0
            ? $"Updated {updated} of {updated + failed} parameter(s); {failed} failed."
            : $"None of {failed} parameter write(s) succeeded.";

        return reasons.Count > 0
            ? head + " " + string.Join(" | ", reasons)
            : head;
    }

    public string GetName() => "Set Elements Parameters";
}
