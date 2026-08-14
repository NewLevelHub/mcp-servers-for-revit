using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services;

public class SetElementParameterEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    public long TargetElementId { get; set; }
    public string ParameterName { get; set; } = string.Empty;
    public object? Value { get; set; }
    public SetElementParameterResult Result { get; private set; } = new();
    public bool TaskCompleted { get; private set; }
    private readonly ManualResetEvent _resetEvent = new(false);

            /// <summary>
        /// Reset wait state before ExternalEvent.Raise. Must be called from the command before RaiseAndWaitForCompletion.
        /// </summary>
        public void Prepare()
        {
            TaskCompleted = false;
            _resetEvent.Reset();
        }
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
            // Do not Reset here - SetParameters/Prepare already Reset before Raise.
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

            var parameter = ElementParameterHelper.FindParameter(element, ParameterName)
                ?? throw new ArgumentException(
                    ElementParameterHelper.DescribeMissingParameter(element, ParameterName));

            using var transaction = new Transaction(doc, "Set Element Parameter");
            transaction.Start();

            ElementParameterHelper.SetParameterValue(parameter, Value, doc);

            transaction.Commit();

            Result = new SetElementParameterResult
            {
                Success = true,
                Message = "Parameter updated successfully.",
                ElementId = TargetElementId,
                ParameterName = parameter.Definition.Name,
                NewDisplayValue = parameter.AsValueString() ?? string.Empty
            };
        }
        catch (Exception ex)
        {
            Result = new SetElementParameterResult
            {
                Success = false,
                Message = ex.Message,
                ElementId = TargetElementId,
                ParameterName = ParameterName
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public string GetName() => "Set Element Parameter";
}
