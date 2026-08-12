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
        /// <summary>Per-item off-wall location hints for door/window facing (before centerline snap).</summary>
        private Dictionary<int, XYZ> _requestedFacingHints;

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
                _requestedFacingHints = new Dictionary<int, XYZ>();
                int requestedCount = CreatedInfo?.Count ?? 0;

                using (Transaction transaction = new Transaction(doc, "Create point-based elements"))
                {
                    var failOpts = transaction.GetFailureHandlingOptions();
                    failOpts.SetFailuresPreprocessor(new SuppressOpeningWarningsPreprocessor());
                    failOpts.SetClearAfterRollback(true);
                    transaction.SetFailureHandlingOptions(failOpts);

                    transaction.Start();
                    IList<Level> levels = doc.GetAllLevels();

                    // Pre-count openings per host wall so multiple doors on one wall are spaced (1/3, 2/3…).
                    var openingsPerWall = new Dictionary<int, int>();
                    var openingSlotOnWall = new Dictionary<int, int>(); // item index → slot 0..n-1
                    for (int i = 0; i < requestedCount; i++)
                    {
                        var preview = CreatedInfo[i];
                        // Strict items keep their exact XY — they must not join the auto-spacing pool,
                        // otherwise one strict door would shift its neighbours to 1/3, 2/3 (REV-149).
                        if (preview != null && preview.StrictLocation) continue;
                        if (preview?.HostWallId <= 0) continue;
                        Element pe = doc.GetElement(new ElementId(preview.HostWallId));
                        if (!(pe is Wall)) continue;
                        if (!openingsPerWall.ContainsKey(preview.HostWallId))
                            openingsPerWall[preview.HostWallId] = 0;
                        openingSlotOnWall[i] = openingsPerWall[preview.HostWallId];
                        openingsPerWall[preview.HostWallId]++;
                    }

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
                        // Also read after creation, to size the swing arc we look for (REV-152).
                        double doorWidthFt = data.Width > 0 ? data.Width / 304.8 : 900.0 / 304.8;

                        if (isHostedOpening)
                        {
                            if (data.HostWallId <= 0)
                            {
                                errors.Add($"[{index}] hostWallId is required for doors/windows. Pass the wall ElementId.");
                                continue;
                            }

                            Element hostElem = doc.GetElement(new ElementId(data.HostWallId));
                            if (!(hostElem is Wall hostWall))
                            {
                                errors.Add($"[{index}] hostWallId {data.HostWallId} is not a valid wall.");
                                continue;
                            }

                            // Keep the caller’s off-wall point for facing side (REV-147).
                            // GetSafeOpeningPointOnWall projects onto the centerline — if we
                            // overwrite LocationPoint first, auto-flip always sees side≈0.
                            XYZ requestedLocPt = JZPoint.ToXYZ(data.LocationPoint);
                            XYZ locPt = requestedLocPt;

                            // REV-149: in strict mode the caller traced the point off a DWG
                            // underlay — never re-host it onto a neighbouring wall.
                            if (data.StrictLocation)
                            {
                                explicitHost = hostWall;
                            }
                            else
                            {
                                explicitHost = ProjectUtils.ResolveHostWallForOpening(
                                    doc, hostWall, locPt, baseLevel, doorWidthFt, out var hostWarn);
                                if (!string.IsNullOrEmpty(hostWarn))
                                    _warnings.Add($"[{index}] {hostWarn}");
                            }

                            if (explicitHost == null)
                            {
                                errors.Add($"[{index}] Could not resolve a host wall near locationPoint.");
                                continue;
                            }

                            int totalOnWall = 1;
                            int slot = 0;
                            if (openingsPerWall.TryGetValue(data.HostWallId, out var planned))
                                totalOnWall = Math.Max(1, planned);
                            if (openingSlotOnWall.TryGetValue(index, out var plannedSlot))
                                slot = plannedSlot;

                            // Honor explicit locationPoint. Only auto-space (1/3, 2/3…) when
                            // several openings share one host in the same request — never force
                            // mid-wall for a single door (that put doors on partition T-junctions).
                            // REV-152: strictLocation means the caller traced the point off a DWG
                            // and auto-spacing would move it. Without this the CAD tools had to
                            // send one opening per call to dodge the reshuffle.
                            bool preferRequested = totalOnWall <= 1 || data.StrictLocation;
                            double spanFraction = preferRequested
                                ? 0.5
                                : (slot + 1.0) / (totalOnWall + 1.0);

                            double strictTolFt = data.StrictToleranceMm > 0
                                ? data.StrictToleranceMm / 304.8
                                : 50.0 / 304.8;

                            locPt = ProjectUtils.GetSafeOpeningPointOnWall(
                                (Wall)explicitHost,
                                locPt,
                                doorWidthFt,
                                out var snapWarn,
                                spanFraction,
                                doc,
                                preferRequested,
                                data.StrictLocation,
                                strictTolFt);

                            if (data.StrictLocation && locPt == null)
                            {
                                // Loud failure beats a door quietly moved half a metre (REV-149).
                                errors.Add($"[{index}] {snapWarn ?? "strictLocation could not be satisfied."}");
                                continue;
                            }

                            if (!string.IsNullOrEmpty(snapWarn))
                                _warnings.Add($"[{index}] {snapWarn}");

                            data.LocationPoint = new JZPoint(
                                locPt.X * 304.8, locPt.Y * 304.8, locPt.Z * 304.8);
                            // REV-152: prefer the explicit off-wall hint. Falling back to the
                            // requested point keeps older callers working, but a strict caller
                            // sends the exact point, so on its own it yields side≈0 and no flip.
                            _requestedFacingHints[index] = data.FacingHintPoint != null
                                ? JZPoint.ToXYZ(data.FacingHintPoint)
                                : requestedLocPt;
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

                            // REV-152: for doors, measure the swing that actually landed in the
                            // model and correct against the DWG. Family HandOrientation
                            // conventions differ, so the old dot-product mirrored whole runs of
                            // doors at once. Falls back to the heuristic when the family draws no
                            // plan swing arc — and says so.
                            bool swingResolvedByMeasurement = false;
                            if (builtInCategory == BuiltInCategory.OST_Doors)
                            {
                                swingResolvedByMeasurement = AlignDoorSwingToCad(
                                    doc, instance, explicitHost as Wall, data, doorWidthFt, index);
                            }

                            bool shouldFlip = !swingResolvedByMeasurement && data.FacingFlipped;
                            if (!swingResolvedByMeasurement && !shouldFlip)
                            {
                                Wall hostWall = instance.Host as Wall;
                                if (hostWall != null)
                                {
                                    LocationCurve locCurve = hostWall.Location as LocationCurve;
                                    if (locCurve != null)
                                    {
                                        // Prefer the caller's off-wall point (swing side hint).
                                        // After GetSafeOpeningPointOnWall, LocationPoint sits on
                                        // the centerline so side≈0 and auto-flip never runs.
                                        XYZ originalPt = _requestedFacingHints != null
                                            && _requestedFacingHints.TryGetValue(index, out var hint)
                                            ? hint
                                            : JZPoint.ToXYZ(data.LocationPoint);
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

                            // REV-149: hand (hinge side) is independent of facing. Without it a
                            // door reads as mirrored against the DWG swing even when the side
                            // it opens toward is right.
                            bool flipHandNow = !swingResolvedByMeasurement && data.HandFlipped;
                            if (!swingResolvedByMeasurement && !flipHandNow && data.HandHintPoint != null)
                            {
                                XYZ hingePt = JZPoint.ToXYZ(data.HandHintPoint);
                                XYZ centerPt = JZPoint.ToXYZ(data.LocationPoint);
                                if (hingePt != null && centerPt != null)
                                {
                                    XYZ toHinge = new XYZ(
                                        hingePt.X - centerPt.X, hingePt.Y - centerPt.Y, 0);
                                    if (toHinge.GetLength() > 1e-9)
                                    {
                                        toHinge = toHinge.Normalize();
                                        // HandOrientation runs hinge → latch on Revit's stock doors,
                                        // but read it live rather than trusting the convention: the
                                        // facing flip above may already have changed it.
                                        XYZ hand = instance.HandOrientation;
                                        if (hand != null && hand.DotProduct(toHinge) > 0)
                                            flipHandNow = true;
                                    }
                                }
                            }

                            if (flipHandNow)
                            {
                                try
                                {
                                    instance.flipHand();
                                    doc.Regenerate();
                                }
                                catch (Exception handEx)
                                {
                                    _warnings.Add($"[{index}] flipHand failed: {handEx.Message}");
                                }
                            }

                            if (data.StrictLocation)
                            {
                                double tolFt = (data.StrictToleranceMm > 0 ? data.StrictToleranceMm : 50.0) / 304.8;
                                XYZ placed = (instance.Location as LocationPoint)?.Point;
                                XYZ wanted = JZPoint.ToXYZ(data.LocationPoint);
                                if (placed != null && wanted != null)
                                {
                                    double driftFt = new XYZ(
                                        placed.X - wanted.X, placed.Y - wanted.Y, 0).GetLength();
                                    if (driftFt > tolFt)
                                    {
                                        // Revit relocated it after hosting — drop it rather than
                                        // report success for an element that is not where the DWG says.
                                        errors.Add(
                                            $"[{index}] strictLocation: Revit placed the opening " +
                                            $"{driftFt * 304.8:F0} mm from the requested point " +
                                            $"(limit {tolFt * 304.8:F0} mm); element removed.");
                                        doc.Delete(instance.Id);
                                        continue;
                                    }
                                }
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

        /// <summary>
        /// REV-152: flips the placed door until its own plan swing arc matches the CAD swing.
        /// </summary>
        /// <remarks>
        /// Reading the arc back is what makes this family-agnostic: facing mirrors the swing side,
        /// hand mirrors the hinge side, and both are decided from the measured geometry rather than
        /// from what HandOrientation is assumed to mean. A second round catches families where the
        /// two flips are not independent. Returns false when nothing could be measured, so the
        /// caller falls back to the old heuristic instead of silently leaving the door mirrored.
        /// </remarks>
        private bool AlignDoorSwingToCad(
            Document doc,
            FamilyInstance instance,
            Wall hostWall,
            PointElement data,
            double doorWidthFt,
            int index)
        {
            XYZ targetSwing = FlattenDirection(data.SwingNormal);
            if (targetSwing == null || hostWall == null)
                return false;

            if (!(uiDoc.ActiveView is ViewPlan planView))
            {
                _warnings.Add(
                    $"[{index}] Active view is not a plan — door swing not verified against CAD.");
                return false;
            }

            if (!(hostWall.Location is LocationCurve hostCurve))
                return false;

            XYZ wallDir = FlattenDirection(
                hostCurve.Curve.GetEndPoint(1) - hostCurve.Curve.GetEndPoint(0));
            if (wallDir == null)
                return false;

            // Opening centre → hinge jamb, as traced from the DWG arc.
            XYZ targetHingeDir = null;
            if (data.HandHintPoint != null && data.LocationPoint != null)
                targetHingeDir = FlattenDirection(
                    JZPoint.ToXYZ(data.HandHintPoint) - JZPoint.ToXYZ(data.LocationPoint));

            var measured = DoorSwingReader.TryRead(instance, planView, wallDir, doorWidthFt);
            if (measured == null)
            {
                _warnings.Add(
                    $"[{index}] No plan swing arc on this door family — swing side falls back to a heuristic and is unverified.");
                return false;
            }

            for (int round = 0; round < 2; round++)
            {
                bool swingWrong = measured.SwingNormal.DotProduct(targetSwing) < 0;
                bool hingeWrong = targetHingeDir != null
                    && measured.HingeDir.DotProduct(targetHingeDir) < 0;

                if (!swingWrong && !hingeWrong)
                    return true;

                try
                {
                    if (swingWrong)
                        instance.flipFacing();
                    if (hingeWrong)
                        instance.flipHand();
                }
                catch (Exception flipEx)
                {
                    _warnings.Add($"[{index}] Door swing flip failed: {flipEx.Message}");
                    return false;
                }

                doc.Regenerate();

                measured = DoorSwingReader.TryRead(instance, planView, wallDir, doorWidthFt);
                if (measured == null)
                {
                    _warnings.Add($"[{index}] Door swing became unreadable after flipping.");
                    return false;
                }
            }

            bool swingStillWrong = measured.SwingNormal.DotProduct(targetSwing) < 0;
            bool hingeStillWrong = targetHingeDir != null
                && measured.HingeDir.DotProduct(targetHingeDir) < 0;
            if (swingStillWrong || hingeStillWrong)
            {
                string what = swingStillWrong && hingeStillWrong ? "swing side and hinge"
                    : swingStillWrong ? "swing side"
                    : "hinge";
                _warnings.Add(
                    $"[{index}] Door {what} still does not match the CAD after flipping — check this door manually.");
            }

            return true;
        }

        /// <summary>Unit XY direction from a JZPoint vector, or null when degenerate.</summary>
        private static XYZ FlattenDirection(JZPoint vector)
        {
            return vector == null ? null : FlattenDirection(JZPoint.ToXYZ(vector));
        }

        private static XYZ FlattenDirection(XYZ vector)
        {
            if (vector == null)
                return null;
            var flat = new XYZ(vector.X, vector.Y, 0);
            return flat.GetLength() < 1e-9 ? null : flat.Normalize();
        }
    }
}
