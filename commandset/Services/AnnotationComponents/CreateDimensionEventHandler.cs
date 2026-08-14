using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Annotation;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.AnnotationComponents;

/// <summary>
///     Handles creation of dimension elements in Revit.
/// </summary>
public class CreateDimensionEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private UIApplication _uiApp;
    private UIDocument UiDoc => _uiApp.ActiveUIDocument;
    private Document Doc => UiDoc.Document;
    private readonly ManualResetEvent _resetEvent = new(false);

    public List<DimensionCreationInfo> DimensionsToCreate { get; private set; }
    public AIResult<List<int>> Result { get; private set; }

    public void SetParameters(List<DimensionCreationInfo> dimensions)
    {
        DimensionsToCreate = dimensions;
        _resetEvent.Reset();
    }

    public void Execute(UIApplication app)
    {
        _uiApp = app;
        try
        {
            var createdDimensionIds = new List<int>();
            // One bad record used to throw out of the whole loop, so records after it
            // were never attempted and the model saw a single opaque failure.
            var failures = new List<string>();
            var index = 0;

            foreach (var dimInfo in DimensionsToCreate)
            {
                index++;
                View view = null;
                if (dimInfo.ViewId > 0)
                    view = Doc.GetElement(new ElementId(dimInfo.ViewId)) as View;

                view ??= Doc.ActiveView;

                using var transaction = new Transaction(Doc, "Create Dimension");
                transaction.Start();

                try
                {
                    var startPoint = DimensionAnnotationHelper.ConvertMmToFeet(dimInfo.StartPoint);
                    var endPoint = DimensionAnnotationHelper.ConvertMmToFeet(dimInfo.EndPoint);
                    var line = DimensionAnnotationHelper.BuildDimensionLine(
                        startPoint,
                        endPoint,
                        dimInfo.LinePoint,
                        dimInfo.OffsetMm);

                    Dimension dimension = null;

                    string reason = null;

                    if (dimInfo.ElementIds != null && dimInfo.ElementIds.Count > 0)
                    {
                        var dimensionDirection = (endPoint - startPoint).Normalize();
                        var references = new ReferenceArray();
                        var missingIds = new List<int>();
                        foreach (var elementId in dimInfo.ElementIds)
                        {
                            var element = Doc.GetElement(new ElementId(elementId));
                            if (element == null)
                            {
                                missingIds.Add(elementId);
                                continue;
                            }

                            foreach (var reference in DimensionAnnotationHelper.GetReferences(
                                         element,
                                         view,
                                         dimensionDirection))
                            {
                                references.Append(reference);
                            }
                        }

                        if (references.Size >= 2)
                        {
                            dimension = Doc.Create.NewDimension(view, line, references);
                        }
                        else if (missingIds.Count > 0)
                        {
                            reason = $"элементы не найдены в модели: {string.Join(", ", missingIds)}";
                        }
                        else
                        {
                            reason =
                                $"из {dimInfo.ElementIds.Count} элемент(ов) получено привязок: {references.Size}, " +
                                "для размера нужно минимум 2";
                        }
                    }
                    else
                    {
                        var dimDirection = (endPoint - startPoint).Normalize();
                        var refArray = new ReferenceArray();
                        var tolerance = dimInfo.PickToleranceMm > 0
                            ? dimInfo.PickToleranceMm
                            : DimensionAnnotationHelper.DefaultPickToleranceMm;

                        var startRef = DimensionAnnotationHelper.FindReferenceAtPoint(
                            Doc, view, startPoint, dimDirection, tolerance, out var startReason);
                        var endRef = DimensionAnnotationHelper.FindReferenceAtPoint(
                            Doc, view, endPoint, dimDirection, tolerance, out var endReason);

                        if (startRef != null && endRef != null)
                        {
                            refArray.Append(startRef);
                            refArray.Append(endRef);
                            dimension = Doc.Create.NewDimension(view, line, refArray);
                        }
                        else if (startRef == null && endRef == null)
                        {
                            reason = $"начало: {startReason}; конец: {endReason}";
                        }
                        else
                        {
                            reason = startRef == null
                                ? $"начало размера: {startReason}"
                                : $"конец размера: {endReason}";
                        }
                    }

                    if (dimension != null)
                    {
                        DimensionAnnotationHelper.ApplyDimensionType(
                            dimension,
                            Doc,
                            dimInfo.DimensionType,
                            dimInfo.DimensionStyleId);
                        ApplyDimensionParameters(dimension, dimInfo);
                        createdDimensionIds.Add(dimension.Id.GetIntValue());
                        transaction.Commit();
                    }
                    else
                    {
                        // Nothing was created — do not leave an empty transaction behind.
                        transaction.RollBack();
                        failures.Add($"#{index}: {reason ?? "размер не создан"}");
                    }
                }
                catch (Exception recordEx)
                {
                    transaction.RollBack();
                    // Keep going: the remaining records may well succeed.
                    failures.Add($"#{index}: {recordEx.Message}");
                }
            }

            var requested = DimensionsToCreate.Count;
            var created = createdDimensionIds.Count;
            var message = $"Создано размеров: {created} из {requested}.";
            if (failures.Count > 0)
                message += " Не удалось — " + string.Join("; ", failures) + ".";

            Result = new AIResult<List<int>>
            {
                // A run that created nothing is a failure, not a success with an empty
                // list: reporting it as success is what made the model repeat the call.
                Success = created > 0,
                Message = message,
                Response = createdDimensionIds
            };
        }
        catch (Exception ex)
        {
            // Never TaskDialog.Show here: Execute runs inside an ExternalEvent with
            // nobody able to click it during an agent-driven turn — it would hang
            // the whole chat instead of returning this Result over the socket.
            Result = new AIResult<List<int>>
            {
                Success = false,
                Message = $"Error creating dimensions: {ex.Message}",
                Response = new List<int>()
            };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
            // Do not Reset here - SetParameters/Prepare already Reset before Raise.
            return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public string GetName() => "Create Dimension";

    private static void ApplyDimensionParameters(Dimension dimension, DimensionCreationInfo dimensionInfo)
    {
        if (dimensionInfo.Options == null)
            return;

        foreach (var option in dimensionInfo.Options)
        {
            var param = dimension.LookupParameter(option.Key);
            if (param == null)
                continue;

            if (option.Value is double doubleValue && param.StorageType == StorageType.Double)
                param.Set(doubleValue * DimensionAnnotationHelper.MillimetersToFeet);
            else if (option.Value is int intValue && param.StorageType == StorageType.Integer)
                param.Set(intValue);
            else if (option.Value is string stringValue && param.StorageType == StorageType.String)
                param.Set(stringValue);
        }
    }
}
