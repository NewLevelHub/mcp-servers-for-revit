using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services;

public class GetElementParametersEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    public long TargetElementId { get; set; }
    public ElementParametersResult Result { get; private set; } = new();
    public bool TaskCompleted { get; private set; }
    private readonly ManualResetEvent _resetEvent = new(false);

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        _resetEvent.Reset();
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("No active Revit document.");

            var element = doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(TargetElementId))
                ?? throw new ArgumentException($"Element with id {TargetElementId} was not found.");

            var parameters = element.Parameters
                .Cast<Parameter>()
                .Where(parameter => parameter?.Definition != null)
                .OrderBy(parameter => parameter.Definition.Name, StringComparer.OrdinalIgnoreCase)
                .Select(parameter => ElementParameterHelper.ToParameterInfo(parameter, doc))
                .ToList();

            Result = new ElementParametersResult
            {
                Success = true,
                Message = $"Collected {parameters.Count} parameters.",
                ElementId = TargetElementId,
                ElementName = element.Name,
                Category = element.Category?.Name ?? string.Empty,
                Parameters = parameters
            };
        }
        catch (Exception ex)
        {
            Result = new ElementParametersResult
            {
                Success = false,
                Message = ex.Message,
                ElementId = TargetElementId,
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public string GetName() => "Get Element Parameters";
}
