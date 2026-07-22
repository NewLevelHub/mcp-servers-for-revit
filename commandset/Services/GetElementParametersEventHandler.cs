using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services;

public class GetElementParametersEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    public long TargetElementId { get; set; }
    public List<string>? ParameterNames { get; set; }
    public bool Slim { get; set; }
    public ElementParametersResult Result { get; private set; } = new();
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
        try
        {
            var doc = app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("No active Revit document.");

            Result = GetElementsParametersEventHandler.CollectElementParameters(
                doc,
                TargetElementId,
                ParameterNames,
                Slim);
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
