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

        public string _wallName = "常规 - ";
        public string _ductName = "矩形风管 - ";

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

                using (Transaction transaction = new Transaction(doc, "Create line-based elements"))
                {
                    transaction.Start();

                    for (int index = 0; index < requestedCount; index++)
                    {
                        var data = CreatedInfo[index];
                        int requestedTypeId = data.TypeId;

                        BuiltInCategory builtInCategory = BuiltInCategory.INVALID;
                        Enum.TryParse(data.Category?.Replace(".", "") ?? "", true, out builtInCategory);

                        Level baseLevel = doc.FindNearestLevel(data.BaseLevel / 304.8);
                        if (baseLevel == null)
                        {
                            errors.Add($"[{index}] No level found near baseLevel={data.BaseLevel} mm.");
                            continue;
                        }

                        double baseOffset = (data.BaseOffset + data.BaseLevel) / 304.8 - baseLevel.Elevation;
                        Level topLevel = doc.FindNearestLevel((data.BaseLevel + data.BaseOffset + data.Height) / 304.8);
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
                                    JZLine.ToLine(data.LocationLine),
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
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        /// <summary>
        /// IExternalEventHandler.GetName 实现
        /// </summary>
        public string GetName()
        {
            return "创建线状构件";
        }

        /// <summary>
        /// 创建或获取指定厚度的墙体类型
        /// </summary>
        /// <param name="doc">Revit文档</param>
        /// <param name="width">宽度（ft）</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        private WallType CreateOrGetWallType(Document doc, double width = 200 / 304.8)
        {
            // 如果没有有效的类型
            // 先查找是否存在指定厚度的建筑墙类型
            WallType existingType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(WallType))
                                    .Cast<WallType>()
                                    .FirstOrDefault(w => w.Name == $"{_wallName}{width * 304.8}mm");
            if (existingType != null)
                return existingType;

            // 不存在则创建新的墙体类型，基于基本墙
            WallType baseWallType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(WallType))
                                    .Cast<WallType>()
                                    .FirstOrDefault(w => w.Name.Contains("常规")); ;
            if (baseWallType == null)
            {
                baseWallType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(WallType))
                                    .Cast<WallType>()
                                    .FirstOrDefault(); ;
            }

            if (baseWallType == null)
                throw new InvalidOperationException("未找到可用的基础墙类型");

            // 复制墙体类型
            WallType newWallType = null;
            newWallType = baseWallType.Duplicate($"{_wallName}{width * 304.8}mm") as WallType;

            // 设置墙厚
            CompoundStructure cs = newWallType.GetCompoundStructure();
            if (cs != null)
            {
                // 获取原始层的材料ID
                ElementId materialId = cs.GetLayers().First().MaterialId;

                // 创建新的单层结构
                CompoundStructureLayer newLayer = new CompoundStructureLayer(
                    width,  // 宽度（转换为英尺）
                    MaterialFunctionAssignment.Structure,  // 功能分配
                    materialId  // 材料ID
                );

                // 创建新的复合结构
                IList<CompoundStructureLayer> newLayers = new List<CompoundStructureLayer> { newLayer };
                cs.SetLayers(newLayers);

                // 应用新的复合结构
                newWallType.SetCompoundStructure(cs);
            }
            return newWallType;
        }

        /// <summary>
        /// 创建或获取指定尺寸的风管类型
        /// </summary>
        /// <param name="doc">Revit文档</param>
        /// <param name="width">宽度（ft）</param>
        /// <param name="height">高度（ft）</param>
        /// <returns>风管类型</returns>
        private DuctType CreateOrGetDuctType(Document doc, double width, double height)
        {
            string typeName = $"{_ductName}{width * 304.8}x{height * 304.8}mm";

            // 先查找是否存在指定尺寸的风管类型
            DuctType existingType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(DuctType))
                                    .Cast<DuctType>()
                                    .FirstOrDefault(d => d.Name == typeName && d.Shape == ConnectorProfileType.Rectangular);

            if (existingType != null)
                return existingType;

            // 不存在则创建新的风管类型，基于已有的矩形风管类型
            DuctType baseDuctType = new FilteredElementCollector(doc)
                                    .OfClass(typeof(DuctType))
                                    .Cast<DuctType>()
                                    .FirstOrDefault(d => d.Shape == ConnectorProfileType.Rectangular);

            if (baseDuctType == null)
                throw new InvalidOperationException("未找到可用的基础矩形风管类型");

            // 复制风管类型
            DuctType newDuctType = baseDuctType.Duplicate(typeName) as DuctType;

            // 设置风管尺寸参数
            Parameter widthParam = newDuctType.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
            Parameter heightParam = newDuctType.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);

            if (widthParam != null && heightParam != null)
            {
                widthParam.Set(width);
                heightParam.Set(height);
            }

            return newDuctType;
        }

    }
}
