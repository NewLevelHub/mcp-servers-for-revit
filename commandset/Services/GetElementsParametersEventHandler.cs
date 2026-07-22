using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services;

public class GetElementsParametersEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    public List<long> TargetElementIds { get; set; } = new();
    public List<string>? ParameterNames { get; set; }
    public bool Slim { get; set; }
    public ElementsParametersBatchResult Result { get; private set; } = new();
    public bool TaskCompleted { get; private set; }
    private readonly ManualResetEvent _resetEvent = new(false);

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
        var results = new List<ElementParametersResult>();
        try
        {
            var doc = app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("No active Revit document.");

            if (TargetElementIds == null || TargetElementIds.Count == 0)
                throw new ArgumentException("elementIds must contain at least one id.");

            foreach (var elementId in TargetElementIds)
            {
                results.Add(CollectElementParameters(doc, elementId, ParameterNames, Slim));
            }

            var failed = results.Count(r => !r.Success);
            Result = new ElementsParametersBatchResult
            {
                Success = failed == 0,
                Message = failed == 0
                    ? $"Collected parameters for {results.Count} elements."
                    : $"Collected parameters for {results.Count - failed}/{results.Count} elements ({failed} failed).",
                Results = results
            };
        }
        catch (Exception ex)
        {
            Result = new ElementsParametersBatchResult
            {
                Success = false,
                Message = ex.Message,
                Results = results
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    internal static ElementParametersResult CollectElementParameters(
        Document doc,
        long targetElementId,
        List<string>? parameterNames,
        bool slim)
    {
        try
        {
            var element = doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(targetElementId))
                ?? throw new ArgumentException($"Element with id {targetElementId} was not found.");

            List<ElementParameterInfo> parameters;
            if (parameterNames != null && parameterNames.Count > 0)
            {
                parameters = new List<ElementParameterInfo>(parameterNames.Count);
                foreach (var name in parameterNames)
                {
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var parameter = ElementParameterHelper.FindParameter(element, name);
                    if (parameter?.Definition == null)
                        continue;

                    parameters.Add(ElementParameterHelper.ToParameterInfo(parameter, doc, slim));
                }
            }
            else
            {
                parameters = element.Parameters
                    .Cast<Parameter>()
                    .Where(parameter => parameter?.Definition != null)
                    .OrderBy(parameter => parameter.Definition.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(parameter => ElementParameterHelper.ToParameterInfo(parameter, doc, slim))
                    .ToList();
            }

            return new ElementParametersResult
            {
                Success = true,
                Message = $"Collected {parameters.Count} parameters.",
                ElementId = targetElementId,
                ElementName = element.Name,
                Category = element.Category?.Name ?? string.Empty,
                Parameters = parameters
            };
        }
        catch (Exception ex)
        {
            return new ElementParametersResult
            {
                Success = false,
                Message = ex.Message,
                ElementId = targetElementId,
            };
        }
    }

    public string GetName() => "Get Elements Parameters";
}
