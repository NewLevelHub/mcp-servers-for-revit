using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services
{
    public class CreatePointElementEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
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
        public List<PointElement> CreatedInfo { get; private set; }
        /// <summary>
        /// 执行结果（传出数据）
        /// </summary>
        public AIResult<List<int>> Result { get; private set; }
        private List<string> _warnings = new List<string>();

        /// <summary>
        /// 设置创建的参数
        /// </summary>
        public void SetParameters(List<PointElement> data)
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

                using (Transaction transaction = new Transaction(doc, "Create point-based elements"))
                {
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

                        if (requestedTypeId == -1 || requestedTypeId == 0)
                        {
                            errors.Add($"[{index}] typeId is required. Call get_available_family_types and pass a valid typeId.");
                            continue;
                        }

                        Element typeEle = doc.GetElement(new ElementId(requestedTypeId));
                        FamilySymbol symbol = typeEle as FamilySymbol;
                        if (symbol == null)
                        {
                            errors.Add($"[{index}] typeId {requestedTypeId} not found or not a FamilySymbol. Call get_available_family_types.");
                            continue;
                        }

                        builtInCategory = (BuiltInCategory)symbol.Category.Id.GetIntValue();

                        bool isHostedOpening = builtInCategory == BuiltInCategory.OST_Doors
                            || builtInCategory == BuiltInCategory.OST_Windows;
                        Element explicitHost = null;

                        if (isHostedOpening)
                        {
                            if (data.HostWallId <= 0)
                            {
                                errors.Add($"[{index}] hostWallId is required for doors/windows. Pass the wall ElementId.");
                                continue;
                            }

                            Element hostElem = doc.GetElement(new ElementId(data.HostWallId));
                            if (!(hostElem is Wall))
                            {
                                errors.Add($"[{index}] hostWallId {data.HostWallId} is not a valid wall.");
                                continue;
                            }
                            explicitHost = hostElem;
                        }
                        else if (data.HostWallId > 0)
                        {
                            Element hostElem = doc.GetElement(new ElementId(data.HostWallId));
                            if (hostElem is Wall)
                                explicitHost = hostElem;
                            else
                                errors.Add($"[{index}] hostWallId {data.HostWallId} is not a valid wall (ignored for non-door/window).");
                        }

                        if (!symbol.IsActive)
                            symbol.Activate();

                        var instance = doc.CreateInstance(
                            symbol,
                            JZPoint.ToXYZ(data.LocationPoint),
                            null,
                            baseLevel,
                            topLevel,
                            baseOffset,
                            topOffset,
                            null,
                            null,
                            null,
                            explicitHost,
                            true);

                        if (instance == null)
                        {
                            errors.Add($"[{index}] CreateInstance returned null.");
                            continue;
                        }

                        if (isHostedOpening)
                        {
                            doc.Regenerate();

                            bool shouldFlip = data.FacingFlipped;
                            if (!shouldFlip)
                            {
                                Wall hostWall = instance.Host as Wall;
                                if (hostWall != null)
                                {
                                    LocationCurve locCurve = hostWall.Location as LocationCurve;
                                    if (locCurve != null)
                                    {
                                        XYZ originalPt = JZPoint.ToXYZ(data.LocationPoint);
                                        XYZ wallStart = locCurve.Curve.GetEndPoint(0);
                                        XYZ wallEnd = locCurve.Curve.GetEndPoint(1);
                                        XYZ wallDir = new XYZ(wallEnd.X - wallStart.X, wallEnd.Y - wallStart.Y, 0).Normalize();
                                        XYZ wallNormal = wallDir.CrossProduct(XYZ.BasisZ).Normalize();

                                        IntersectionResult ir = locCurve.Curve.Project(originalPt);
                                        if (ir != null)
                                        {
                                            XYZ centerPt = ir.XYZPoint;
                                            double side = (originalPt - centerPt).DotProduct(wallNormal);
                                            double facingDot = instance.FacingOrientation.DotProduct(wallNormal);
                                            if ((side < -1e-10 && facingDot > 0) ||
                                                (side > 1e-10 && facingDot < 0))
                                            {
                                                shouldFlip = true;
                                            }
                                        }
                                    }
                                }
                            }

                            if (shouldFlip)
                            {
                                instance.flipFacing();
                                doc.Regenerate();
                            }
                        }

                        if (data.Rotation != 0 && !isHostedOpening)
                        {
                            XYZ origin = JZPoint.ToXYZ(data.LocationPoint);
                            Line rotationAxis = Line.CreateBound(origin, origin + XYZ.BasisZ);
                            double angleRadians = data.Rotation * Math.PI / 180.0;
                            ElementTransformUtils.RotateElement(doc, instance.Id, rotationAxis, angleRadians);
                        }

                        elementIds.Add(instance.Id.GetIntValue());
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
                    Message = $"Error creating point-based elements: {ex.Message}",
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
            return "创建点状构件";
        }

    }
}
