using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class CreateSurfaceElementEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
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
        public List<SurfaceElement> CreatedInfo { get; private set; }
        /// <summary>
        /// 执行结果（传出数据）
        /// </summary>
        public AIResult<List<int>> Result { get; private set; }
        public string _floorName = "常规 - ";
        public bool _structural = true;
        private List<string> _warnings = new List<string>();

        /// <summary>
        /// 设置创建的参数
        /// </summary>
        public void SetParameters(List<SurfaceElement> data)
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

                using (Transaction transaction = new Transaction(doc, "Create surface-based elements"))
                {
                    transaction.Start();

                    for (int index = 0; index < requestedCount; index++)
                    {
                        var data = CreatedInfo[index];
                        int requestedTypeId = data.TypeId;

                        BuiltInCategory builtInCategory = BuiltInCategory.INVALID;
                        Enum.TryParse(data.Category?.Replace(".", "").Replace("BuiltInCategory", "") ?? "", true, out builtInCategory);

                        Level baseLevel = doc.FindNearestLevel(data.BaseLevel / 304.8);
                        if (baseLevel == null)
                        {
                            errors.Add($"[{index}] No level found near baseLevel={data.BaseLevel} mm.");
                            continue;
                        }

                        double baseOffset = (data.BaseOffset + data.BaseLevel) / 304.8 - baseLevel.Elevation;

                        FloorType floorType = null;
                        RoofType roofType = null;
                        CeilingType ceilingType = null;

                        if (requestedTypeId == -1 || requestedTypeId == 0)
                        {
                            errors.Add($"[{index}] typeId is required. Call get_available_family_types and pass a valid typeId.");
                            continue;
                        }

                        Element typeEle = doc.GetElement(new ElementId(requestedTypeId));
                        if (typeEle is FloorType ft)
                        {
                            floorType = ft;
                            builtInCategory = (BuiltInCategory)floorType.Category.Id.GetIntValue();
                        }
                        else if (typeEle is RoofType rt)
                        {
                            roofType = rt;
                            builtInCategory = (BuiltInCategory)roofType.Category.Id.GetIntValue();
                        }
                        else if (typeEle is CeilingType ct)
                        {
                            ceilingType = ct;
                            builtInCategory = (BuiltInCategory)ceilingType.Category.Id.GetIntValue();
                        }
                        else if (typeEle is FamilySymbol)
                        {
                            errors.Add($"[{index}] typeId {requestedTypeId} is a FamilySymbol; floors/roofs/ceilings need FloorType/RoofType/CeilingType.");
                            continue;
                        }
                        else
                        {
                            errors.Add($"[{index}] typeId {requestedTypeId} not found. Call get_available_family_types.");
                            continue;
                        }

                        if (data.Boundary?.OuterLoop == null || data.Boundary.OuterLoop.Count < 3)
                        {
                            errors.Add($"[{index}] boundary.outerLoop requires at least 3 segments.");
                            continue;
                        }

                        switch (builtInCategory)
                        {
                            case BuiltInCategory.OST_Floors:
                                if (floorType == null)
                                {
                                    errors.Add($"[{index}] typeId {requestedTypeId} is not a FloorType.");
                                    continue;
                                }
                                CurveArray curves = new CurveArray();
                                foreach (var jzLine in data.Boundary.OuterLoop)
                                    curves.Append(JZLine.ToLine(jzLine));
                                CurveLoop curveLoop = CurveLoop.Create(data.Boundary.OuterLoop.Select(l => JZLine.ToLine(l) as Curve).ToList());

#if REVIT2023_OR_GREATER
                                Floor floor = Floor.Create(doc, new List<CurveLoop> { curveLoop }, floorType.Id, baseLevel.Id);
#else
                                Floor floor = doc.Create.NewFloor(curves, floorType, baseLevel, _structural);
#endif
                                if (floor != null)
                                {
                                    floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM).Set(baseOffset);
                                    elementIds.Add(floor.Id.GetIntValue());
                                }
                                else
                                    errors.Add($"[{index}] Floor.Create returned null.");
                                break;

                            case BuiltInCategory.OST_Roofs:
                                if (roofType == null)
                                {
                                    errors.Add($"[{index}] typeId {requestedTypeId} is not a RoofType.");
                                    continue;
                                }
                                CurveArray roofCurves = new CurveArray();
                                foreach (var jzLine in data.Boundary.OuterLoop)
                                    roofCurves.Append(JZLine.ToLine(jzLine));

                                ModelCurveArray modelCurves = new ModelCurveArray();
                                FootPrintRoof roof = doc.Create.NewFootPrintRoof(roofCurves, baseLevel, roofType, out modelCurves);
                                if (roof != null)
                                {
                                    foreach (ModelCurve mc in modelCurves)
                                        roof.set_DefinesSlope(mc, false);
                                    Parameter offsetParam = roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM);
                                    if (offsetParam != null)
                                        offsetParam.Set(baseOffset);
                                    elementIds.Add(roof.Id.GetIntValue());
                                }
                                else
                                    errors.Add($"[{index}] NewFootPrintRoof returned null.");
                                break;

                            case BuiltInCategory.OST_Ceilings:
                                if (ceilingType == null)
                                {
                                    errors.Add($"[{index}] typeId {requestedTypeId} is not a CeilingType.");
                                    continue;
                                }
                                CurveLoop ceilingCurveLoop = CurveLoop.Create(data.Boundary.OuterLoop.Select(l => JZLine.ToLine(l) as Curve).ToList());
#if REVIT2022_OR_GREATER
                                Ceiling ceiling = Ceiling.Create(doc, new List<CurveLoop> { ceilingCurveLoop }, ceilingType.Id, baseLevel.Id);
#else
                                Ceiling ceiling = null;
                                errors.Add($"[{index}] Ceiling creation is not supported before Revit 2022.");
#endif
                                if (ceiling != null)
                                {
                                    Parameter ceilingOffsetParam = ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM);
                                    if (ceilingOffsetParam != null)
                                        ceilingOffsetParam.Set(baseOffset);
                                    elementIds.Add(ceiling.Id.GetIntValue());
                                }
                                break;

                            default:
                                errors.Add($"[{index}] Unsupported surface category {builtInCategory}.");
                                break;
                        }
                    }

                    transaction.Commit();
                }

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
                    Message = $"Error creating surface-based elements: {ex.Message}",
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
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        /// <summary>
        /// IExternalEventHandler.GetName 实现
        /// </summary>
        public string GetName()
        {
            return "创建面状构件";
        }

        /// <summary>
        /// 获取或创建指定厚度的楼板类型
        /// </summary>
        /// <param name="thickness">目标厚度（ft）</param>
        /// <returns>符合厚度要求的楼板类型</returns>
        private FloorType CreateOrGetFloorType(Document doc, double thickness = 200 / 304.8)
        {

            // 查找匹配厚度的楼板类型
            FloorType existingType = new FilteredElementCollector(doc)
                                     .OfClass(typeof(FloorType))                    // 仅获取FloorType类
                                     .OfCategory(BuiltInCategory.OST_Floors)        // 仅获取楼板类别
                                     .Cast<FloorType>()                            // 转换为FloorType类型
                                     .FirstOrDefault(w => w.Name == $"{_floorName}{thickness * 304.8}mm");
            if (existingType != null)
                return existingType;
            // 如果没有找到匹配的楼板类型，创建新的
            FloorType baseFloorType = existingType = new FilteredElementCollector(doc)
                                     .OfClass(typeof(FloorType))                    // 仅获取FloorType类
                                     .OfCategory(BuiltInCategory.OST_Floors)        // 仅获取楼板类别
                                     .Cast<FloorType>()                            // 转换为FloorType类型
                                     .FirstOrDefault(w => w.Name.Contains("常规"));
            if (existingType != null)
            {
                baseFloorType = existingType = new FilteredElementCollector(doc)
                                     .OfClass(typeof(FloorType))                    // 仅获取FloorType类
                                     .OfCategory(BuiltInCategory.OST_Floors)        // 仅获取楼板类别
                                     .Cast<FloorType>()                            // 转换为FloorType类型
                                     .FirstOrDefault();
            }

            // 复制楼板类型
            FloorType newFloorType = null;
            newFloorType = baseFloorType.Duplicate($"{_floorName}{thickness * 304.8}mm") as FloorType;

            // 设置新楼板类型的厚度
            // 获取构造层设置
            CompoundStructure cs = newFloorType.GetCompoundStructure();
            if (cs != null)
            {
                // 获取所有层
                IList<CompoundStructureLayer> layers = cs.GetLayers();
                if (layers.Count > 0)
                {
                    // 计算当前总厚度
                    double currentTotalThickness = cs.GetWidth();

                    // 按比例调整每层厚度
                    for (int i = 0; i < layers.Count; i++)
                    {
                        CompoundStructureLayer layer = layers[i];
                        double newLayerThickness = thickness;
                        cs.SetLayerWidth(i, newLayerThickness);
                    }

                    // 应用修改后的构造层设置
                    newFloorType.SetCompoundStructure(cs);
                }
            }
            return newFloorType;
        }

    }
}
