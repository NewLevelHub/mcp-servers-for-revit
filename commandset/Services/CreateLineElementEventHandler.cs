using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class CreateLineElementEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication uiApp;
        private UIDocument uiDoc => uiApp.ActiveUIDocument;
        private Document doc => uiDoc.Document;
        private Autodesk.Revit.ApplicationServices.Application app => uiApp.Application;
        /// <summary>
        /// 事件等待对象
        /// </summary>
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);
        /// <summary>
        /// 创建数据（传入数据）
        /// </summary>
        public List<LineElement> CreatedInfo { get; private set; }
        /// <summary>
        /// 执行结果（传出数据）
        /// </summary>
        public AIResult<List<int>> Result { get; private set; }
        private List<string> _warnings = new List<string>();

        /// <summary>
        /// 设置创建的参数
        /// </summary>
        public void SetParameters(List<LineElement> data)
        {
            CreatedInfo = data;
            _resetEvent.Reset();
        }
        public void Execute(UIApplication uiapp)
        {
            uiApp = uiapp;

            try
            {
                var elementIds = new List<int>();
                var errors = new List<string>();
                _warnings.Clear();
                int requestedCount = CreatedInfo?.Count ?? 0;

                var warningRecorder = new RecordingWarningsPreprocessor();

                using (Transaction transaction = new Transaction(doc, "Create line-based elements"))
                {
                    // Overlapping traced walls raise a modal warning on commit. Inside an
                    // ExternalEvent nobody can click it: the batch hung ~41 s, and a "Cancel"
                    // click rolled all 15 walls back so they never appeared (REV-151).
                    var failOpts = transaction.GetFailureHandlingOptions();
                    failOpts.SetFailuresPreprocessor(warningRecorder);
                    failOpts.SetClearAfterRollback(true);
                    transaction.SetFailureHandlingOptions(failOpts);

                    transaction.Start();
                    IList<Level> levels = doc.GetAllLevels();

                    for (int index = 0; index < requestedCount; index++)
                    {
                        var data = CreatedInfo[index];
                        int requestedTypeId = data.TypeId;

                        BuiltInCategory builtInCategory = BuiltInCategory.INVALID;
                        Enum.TryParse(data.Category?.Replace(".", "") ?? "", true, out builtInCategory);

                        Level baseLevel = ProjectUtils.FindNearestLevel(levels, data.BaseLevel / 304.8);
                        if (baseLevel == null)
                        {
                            errors.Add($"[{index}] No level found near baseLevel={data.BaseLevel} mm.");
                            continue;
                        }

                        double baseOffset = (data.BaseOffset + data.BaseLevel) / 304.8 - baseLevel.Elevation;
                        Level topLevel = ProjectUtils.FindNearestLevel(levels, (data.BaseLevel + data.BaseOffset + data.Height) / 304.8);
                        double topOffset = (data.BaseLevel + data.BaseOffset + data.Height) / 304.8 - topLevel.Elevation;

                        FamilySymbol symbol = null;
                        WallType wallType = null;
                        DuctType ductType = null;

                        if (requestedTypeId == -1 || requestedTypeId == 0)
                        {
                            errors.Add($"[{index}] typeId is required. Call get_available_family_types and pass a valid typeId.");
                            continue;
                        }

                        Element typeEle = doc.GetElement(new ElementId(requestedTypeId));
                        if (typeEle is FamilySymbol fs)
                        {
                            symbol = fs;
                            builtInCategory = (BuiltInCategory)symbol.Category.Id.GetIntValue();
                        }
                        else if (typeEle is WallType wt)
                        {
                            wallType = wt;
                            builtInCategory = (BuiltInCategory)wallType.Category.Id.GetIntValue();
                        }
                        else if (typeEle is DuctType dt)
                        {
                            ductType = dt;
                            builtInCategory = (BuiltInCategory)ductType.Category.Id.GetIntValue();
                        }
                        else
                        {
                            errors.Add($"[{index}] typeId {requestedTypeId} not found. Call get_available_family_types.");
                            continue;
                        }

                        if (builtInCategory == BuiltInCategory.INVALID)
                        {
                            errors.Add($"[{index}] Invalid category for typeId {requestedTypeId}.");
                            continue;
                        }

                        switch (builtInCategory)
                        {
                            case BuiltInCategory.OST_Walls:
                                if (wallType == null)
                                {
                                    errors.Add($"[{index}] typeId {requestedTypeId} is not a WallType.");
                                    continue;
                                }
                                Wall wall = Wall.Create(
                                    doc,
                                    JZLine.ToCurve(data.LocationLine),
                                    wallType.Id,
                                    baseLevel.Id,
                                    data.Height / 304.8,
                                    baseOffset,
                                    false,
                                    false);
                                if (wall != null)
                                    elementIds.Add(wall.Id.GetIntValue());
                                else
                                    errors.Add($"[{index}] Wall.Create returned null.");
                                break;

                            case BuiltInCategory.OST_DuctCurves:
                                if (ductType == null)
                                {
                                    errors.Add($"[{index}] typeId {requestedTypeId} is not a DuctType.");
                                    continue;
                                }
                                MEPSystemType mepSystemType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(MEPSystemType))
                                    .Cast<MEPSystemType>()
                                    .FirstOrDefault(m => m.SystemClassification == MEPSystemClassification.SupplyAir);
                                if (mepSystemType == null)
                                {
                                    errors.Add($"[{index}] No SupplyAir MEPSystemType in project.");
                                    continue;
                                }
                                Line ductLine = JZLine.ToLine(data.LocationLine);
                                Duct duct = Duct.Create(
                                    doc,
                                    mepSystemType.Id,
                                    ductType.Id,
                                    baseLevel.Id,
                                    ductLine.GetEndPoint(0),
                                    ductLine.GetEndPoint(1));
                                if (duct != null)
                                {
                                    Parameter offsetParam = duct.get_Parameter(BuiltInParameter.RBS_OFFSET_PARAM);
                                    if (offsetParam != null)
                                        offsetParam.Set(baseOffset);
                                    elementIds.Add(duct.Id.GetIntValue());
                                }
                                else
                                    errors.Add($"[{index}] Duct.Create returned null.");
                                break;

                            default:
                                if (symbol == null)
                                {
                                    errors.Add($"[{index}] typeId {requestedTypeId} is not a FamilySymbol for {builtInCategory}.");
                                    continue;
                                }
                                if (!symbol.IsActive)
                                    symbol.Activate();
                                var instance = doc.CreateInstance(symbol, null, JZLine.ToLine(data.LocationLine), baseLevel, topLevel, baseOffset, topOffset);
                                if (instance != null)
                                    elementIds.Add(instance.Id.GetIntValue());
                                else
                                    errors.Add($"[{index}] CreateInstance returned null.");
                                break;
                        }
                    }

                    transaction.Commit();
                }

                // Dismissed, not hidden: "walls overlap" means the traced axes doubled up and
                // the caller has to see it.
                if (warningRecorder.HasDismissals)
                    _warnings.AddRange(warningRecorder.ToWarningLines("Revit warning (auto-dismissed)"));

                bool success = errors.Count == 0 && elementIds.Count == requestedCount;
                string message = success
                    ? $"Successfully created {elementIds.Count} element(s)."
                    : $"Created {elementIds.Count}/{requestedCount} element(s) with {errors.Count} error(s).";
                if (errors.Count > 0)
                    message += "\n\nErrors:\n  • " + string.Join("\n  • ", errors);
                if (_warnings.Count > 0)
                    message += "\n\nWarnings:\n  • " + string.Join("\n  • ", _warnings);

                Result = new AIResult<List<int>>
                {
                    Success = success,
                    Message = message,
                    Response = elementIds,
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<List<int>>
                {
                    Success = false,
                    Message = $"Error creating line-based elements: {ex.Message}",
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        /// <summary>
        /// 等待创建完成
        /// </summary>
        /// <param name="timeoutMilliseconds">超时时间（毫秒）</param>
        /// <returns>操作是否在超时前完成</returns>
        public bool WaitForCompletion(int timeoutMilliseconds = 60000)
        {
            // Do not Reset here — SetParameters already Reset; Execute Sets when done.
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        /// <summary>
        /// IExternalEventHandler.GetName 实现
        /// </summary>
        public string GetName()
        {
            return "创建线状构件";
        }
    }
}
