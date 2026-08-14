using Autodesk.Revit.DB;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents;

public static class DimensionAnnotationHelper
{
    public const double MillimetersToFeet = 1.0 / 304.8;
    public const double DefaultOffsetMm = 304.8;
    public const double DefaultPickToleranceMm = 1524;

    public static double ResolveOffsetMm(double offsetMm)
    {
        return offsetMm > 0 ? offsetMm : DefaultOffsetMm;
    }

    public static XYZ ConvertMmToFeet(double x, double y, double z)
    {
        return new XYZ(x * MillimetersToFeet, y * MillimetersToFeet, z * MillimetersToFeet);
    }

    public static XYZ ConvertMmToFeet(JZPoint point)
    {
        return ConvertMmToFeet(point.X, point.Y, point.Z);
    }

    public static DimensionType ResolveDimensionType(Document doc, string dimensionTypeName, int dimensionStyleId)
    {
        if (dimensionStyleId > 0)
        {
            var byId = doc.GetElement(new ElementId(dimensionStyleId)) as DimensionType;
            if (byId != null)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(dimensionTypeName))
        {
            var trimmed = dimensionTypeName.Trim();
            var types = new FilteredElementCollector(doc)
                .OfClass(typeof(DimensionType))
                .Cast<DimensionType>()
                .ToList();

            var exact = types.FirstOrDefault(
                type => type.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;

            var partial = types.FirstOrDefault(
                type => type.Name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0);
            if (partial != null)
                return partial;
        }

        // Prefer project working-drawing styles (ADSK) over the first arbitrary linear type.
        var linearTypes = new FilteredElementCollector(doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .Where(type => type.StyleType == DimensionStyleType.Linear)
            .ToList();

        string[] preferredNames =
        {
            "ADSK_Основной_2.5 мм",
            "ADSK_Основной_2 мм",
            "ADSK_Основной_3.5 мм"
        };

        foreach (var preferred in preferredNames)
        {
            var match = linearTypes.FirstOrDefault(
                type => type.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        var adskMain = linearTypes.FirstOrDefault(
            type => type.Name.IndexOf("ADSK_Основной", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    type.Name.IndexOf("Выноска", StringComparison.OrdinalIgnoreCase) < 0 &&
                    type.Name.IndexOf("Округление", StringComparison.OrdinalIgnoreCase) < 0);
        if (adskMain != null)
            return adskMain;

        return linearTypes.FirstOrDefault();
    }

    public static void ApplyDimensionType(
        Dimension dimension,
        Document doc,
        string dimensionTypeName,
        int dimensionStyleId)
    {
        // Always resolve — empty name picks ADSK working-drawing linear type when present.
        var dimensionType = ResolveDimensionType(doc, dimensionTypeName, dimensionStyleId);
        if (dimensionType == null || dimensionType.StyleType != DimensionStyleType.Linear)
            return;

        try
        {
            dimension.DimensionType = dimensionType;
        }
        catch
        {
            // Keep the dimension with Revit default type when assignment is incompatible.
        }
    }

    public static Line BuildDimensionLine(XYZ startPoint, XYZ endPoint, JZPoint linePoint, double offsetMm)
    {
        var direction = (endPoint - startPoint).Normalize();
        if (direction.GetLength() < 1e-9)
            direction = XYZ.BasisX;

        var anchor = linePoint != null
            ? ConvertMmToFeet(linePoint)
            : ComputeDefaultLineAnchor(startPoint, endPoint, direction, offsetMm);

        var halfLength = startPoint.DistanceTo(endPoint) / 2.0;
        return Line.CreateBound(
            anchor - direction * halfLength,
            anchor + direction * halfLength);
    }

    public static Reference FindReferenceAtPoint(
        Document doc,
        View view,
        XYZ point,
        XYZ dimensionDirection,
        double pickToleranceMm = DefaultPickToleranceMm)
    {
        return FindReferenceAtPoint(doc, view, point, dimensionDirection, pickToleranceMm, out _);
    }

    /// <summary>
    ///     As <see cref="FindReferenceAtPoint(Document, View, XYZ, XYZ, double)"/>, but states
    ///     why nothing was found. Returning a bare null told the model only "failed", so it
    ///     retried the same call with guessed coordinates instead of widening the tolerance
    ///     or dimensioning by element id.
    /// </summary>
    public static Reference FindReferenceAtPoint(
        Document doc,
        View view,
        XYZ point,
        XYZ dimensionDirection,
        double pickToleranceMm,
        out string failureReason)
    {
        failureReason = null;
        // Restrict to dimensionable categories — scanning every view element is very slow on large plans.
        var dimensionCategories = new List<BuiltInCategory>
        {
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Grids,
            BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_Columns,
            BuiltInCategory.OST_StructuralFraming,
            BuiltInCategory.OST_Lines,
            BuiltInCategory.OST_RoomSeparationLines,
            BuiltInCategory.OST_Stairs,
            BuiltInCategory.OST_Doors,
            BuiltInCategory.OST_Windows,
        };

        var collector = new FilteredElementCollector(doc, view.Id)
            .WhereElementIsNotElementType()
            .WherePasses(new ElementMulticategoryFilter(dimensionCategories));

        Element closestElement = null;
        var minDistance = double.MaxValue;
        var toleranceFeet = pickToleranceMm * MillimetersToFeet;

        foreach (var element in collector)
        {
            if (element.Location == null)
                continue;

            XYZ elementPoint = null;
            if (element.Location is LocationPoint locationPoint)
            {
                elementPoint = locationPoint.Point;
            }
            else if (element.Location is LocationCurve locationCurve)
            {
                elementPoint = locationCurve.Curve.Project(point).XYZPoint;
            }
            else
            {
                continue;
            }

            var distance = point.DistanceTo(elementPoint);
            if (distance < minDistance)
            {
                closestElement = element;
                minDistance = distance;
            }
        }

        if (closestElement == null)
        {
            failureReason =
                $"в точке ({point.X / MillimetersToFeet:F0}, {point.Y / MillimetersToFeet:F0}) мм " +
                "на виде нет элементов, к которым можно привязать размер " +
                "(ищем стены, оси, колонны, балки, линии, лестницы, двери, окна)";
            return null;
        }

        if (minDistance > toleranceFeet)
        {
            failureReason =
                $"ближайший подходящий элемент — {DescribeElement(closestElement)} на расстоянии " +
                $"{minDistance / MillimetersToFeet:F0} мм, это дальше допуска {pickToleranceMm:F0} мм; " +
                "увеличьте pickToleranceMm или задайте elementIds";
            return null;
        }

        var refs = GetReferences(closestElement, view, dimensionDirection);
        if (refs.Count == 0)
        {
            failureReason =
                $"у элемента {DescribeElement(closestElement)} нет граней, пригодных для размера " +
                "в этом направлении";
            return null;
        }

        return refs[0];
    }

    private static string DescribeElement(Element element)
    {
        if (element == null)
            return "неизвестный элемент";
        var category = element.Category?.Name;
        return string.IsNullOrWhiteSpace(category)
            ? $"id {element.Id.GetIntValue()}"
            : $"{category} (id {element.Id.GetIntValue()})";
    }

    /// <summary>
    ///     Wall face for dimensioning: interior (room-side, closest to
    ///     <paramref name="preferNearPoint"/>) or exterior (far side / outer finish).
    /// </summary>
    public static Reference GetBestWallReference(
        Wall wall,
        View view,
        XYZ measureDirection,
        XYZ preferNearPoint,
        bool preferExterior = false)
    {
        if (preferExterior)
        {
            var exterior = FindExteriorSideWallFace(wall, view, measureDirection, preferNearPoint);
            if (exterior != null)
                return exterior;
        }
        else
        {
            var roomSide = FindRoomSideWallFace(wall, view, measureDirection, preferNearPoint);
            if (roomSide != null)
                return roomSide;
        }

        var refs = GetReferences(wall, view, measureDirection);
        return refs.Count > 0 ? refs[0] : null;
    }

    /// <summary>
    ///     Outer finish face: among vertical faces aligned with measure, pick the one
    ///     farthest from the room center (opposite of interior/clear dimensions).
    /// </summary>
    public static Reference FindExteriorSideWallFace(
        Wall wall,
        View view,
        XYZ measureDirection,
        XYZ preferNearPoint)
    {
        if (wall == null || preferNearPoint == null)
            return null;

        Reference bestRef = null;
        var bestDistance = double.MinValue;
        var bestAlignment = -1.0;

        foreach (var reference in EnumerateWallSideFaceReferences(wall, ShellLayerType.Exterior)
                     .Concat(EnumerateWallSideFaceReferences(wall)))
        {
            if (wall.GetGeometryObjectFromReference(reference) is not PlanarFace planarFace)
                continue;

            var normal = planarFace.FaceNormal;
            if (Math.Abs(normal.Z) > 0.9)
                continue;

            var alignment = measureDirection == null
                ? 1.0
                : Math.Abs(normal.DotProduct(measureDirection));
            if (alignment < 0.85)
                continue;

            var facePoint = planarFace.Origin;
            var distance = preferNearPoint.DistanceTo(
                new XYZ(facePoint.X, facePoint.Y, preferNearPoint.Z));

            if (alignment > bestAlignment + 1e-6
                || (Math.Abs(alignment - bestAlignment) <= 1e-6 && distance > bestDistance))
            {
                bestAlignment = alignment;
                bestDistance = distance;
                bestRef = reference;
            }
        }

        return bestRef;
    }

    public static Reference FindRoomSideWallFace(
        Wall wall,
        View view,
        XYZ measureDirection,
        XYZ preferNearPoint)
    {
        if (wall == null || preferNearPoint == null)
            return null;

        Reference bestRef = null;
        var bestDistance = double.MaxValue;
        var bestAlignment = -1.0;

        foreach (var reference in EnumerateWallSideFaceReferences(wall))
        {
            if (wall.GetGeometryObjectFromReference(reference) is not PlanarFace planarFace)
                continue;

            var normal = planarFace.FaceNormal;
            if (Math.Abs(normal.Z) > 0.9)
                continue;

            var alignment = measureDirection == null
                ? 1.0
                : Math.Abs(normal.DotProduct(measureDirection));
            if (alignment < 0.85)
                continue;

            var facePoint = planarFace.Origin;
            var distance = preferNearPoint.DistanceTo(
                new XYZ(facePoint.X, facePoint.Y, preferNearPoint.Z));

            // Prefer better alignment, then closer to the room interior.
            if (alignment > bestAlignment + 1e-6
                || (Math.Abs(alignment - bestAlignment) <= 1e-6 && distance < bestDistance))
            {
                bestAlignment = alignment;
                bestDistance = distance;
                bestRef = reference;
            }
        }

        if (bestRef != null)
            return bestRef;

        // Fall back to view geometry when side-face API yielded nothing usable.
        return FindRoomSideFaceFromGeometry(wall, view, measureDirection, preferNearPoint);
    }

    private static IEnumerable<Reference> EnumerateWallSideFaceReferences(Wall wall)
    {
        foreach (var layer in new[] { ShellLayerType.Interior, ShellLayerType.Exterior })
        {
            foreach (var face in EnumerateWallSideFaceReferences(wall, layer))
                yield return face;
        }
    }

    private static Reference FindRoomSideFaceFromGeometry(
        Wall wall,
        View view,
        XYZ measureDirection,
        XYZ preferNearPoint)
    {
        var options = new Options
        {
            View = view,
            ComputeReferences = true
        };

        var geometry = wall.get_Geometry(options);
        if (geometry == null)
            return null;

        Reference bestRef = null;
        var bestDistance = double.MaxValue;
        var bestAlignment = -1.0;

        foreach (var obj in geometry)
        {
            if (obj is not Solid solid || solid.Faces.Size <= 0)
                continue;

            foreach (Face face in solid.Faces)
            {
                if (face is not PlanarFace planarFace || face.Reference == null)
                    continue;

                var normal = planarFace.FaceNormal;
                if (Math.Abs(normal.Z) > 0.9)
                    continue;

                var alignment = measureDirection == null
                    ? 1.0
                    : Math.Abs(normal.DotProduct(measureDirection));
                if (alignment < 0.85)
                    continue;

                var facePoint = planarFace.Origin;
                var distance = preferNearPoint.DistanceTo(
                    new XYZ(facePoint.X, facePoint.Y, preferNearPoint.Z));

                if (alignment > bestAlignment + 1e-6
                    || (Math.Abs(alignment - bestAlignment) <= 1e-6 && distance < bestDistance))
                {
                    bestAlignment = alignment;
                    bestDistance = distance;
                    bestRef = face.Reference;
                }
            }
        }

        return bestRef;
    }

    public static List<Reference> GetReferences(Element element, View view, XYZ dimensionDirection = null)
    {
        var references = new List<Reference>();

        if (element is Wall wall)
        {
            var options = new Options
            {
                View = view,
                ComputeReferences = true
            };

            var geometry = wall.get_Geometry(options);
            if (geometry != null)
            {
                Reference bestRef = null;
                var bestAlignment = -1.0;

                foreach (var obj in geometry)
                {
                    if (obj is not Solid solid || solid.Faces.Size <= 0)
                        continue;

                    foreach (Face face in solid.Faces)
                    {
                        if (face is not PlanarFace planarFace)
                            continue;

                        var normal = planarFace.FaceNormal;
                        if (Math.Abs(normal.Z) > 0.9)
                            continue;

                        if (dimensionDirection != null)
                        {
                            var alignment = Math.Abs(normal.DotProduct(dimensionDirection));
                            if (alignment > bestAlignment)
                            {
                                bestAlignment = alignment;
                                bestRef = face.Reference;
                            }
                        }
                        else
                        {
                            references.Add(face.Reference);
                            return references;
                        }
                    }
                }

                if (bestRef != null)
                    references.Add(bestRef);
            }

            if (references.Count == 0)
                references.Add(new Reference(wall));
        }
        else if (element is FamilyInstance familyInstance)
        {
            var options = new Options
            {
                View = view,
                ComputeReferences = true
            };

            var geometry = familyInstance.get_Geometry(options);
            if (geometry != null && dimensionDirection != null)
            {
                Reference bestRef = null;
                var bestAlignment = -1.0;

                foreach (var obj in geometry)
                {
                    var solids = new List<Solid>();
                    if (obj is Solid solid && solid.Faces.Size > 0)
                        solids.Add(solid);
                    else if (obj is GeometryInstance geometryInstance)
                    {
                        foreach (var subObj in geometryInstance.GetInstanceGeometry())
                        {
                            if (subObj is Solid subSolid && subSolid.Faces.Size > 0)
                                solids.Add(subSolid);
                        }
                    }

                    foreach (var solidBody in solids)
                    {
                        foreach (Face face in solidBody.Faces)
                        {
                            if (face is not PlanarFace planarFace)
                                continue;

                            if (Math.Abs(planarFace.FaceNormal.Z) > 0.9)
                                continue;

                            var alignment = Math.Abs(planarFace.FaceNormal.DotProduct(dimensionDirection));
                            if (alignment > bestAlignment)
                            {
                                bestAlignment = alignment;
                                bestRef = face.Reference;
                            }
                        }
                    }
                }

                if (bestRef != null)
                {
                    references.Add(bestRef);
                    return references;
                }
            }

            references.Add(new Reference(familyInstance));
        }
        else
        {
            references.Add(new Reference(element));
        }

        return references;
    }

    /// <summary>
    ///     Both jamb faces of a door/window along <paramref name="measureDirection"/>,
    ///     ordered by position. Prefers stable <see cref="FamilyInstanceReferenceType"/>
    ///     Left/Right (instance geometry often has null Face.Reference).
    /// </summary>
    public static List<(Reference Reference, double PositionMm)> GetOpeningJambReferences(
        FamilyInstance instance,
        View view,
        XYZ measureDirection,
        double minAlignment = 0.85)
    {
        if (instance == null || measureDirection == null || measureDirection.GetLength() < 1e-9)
            return new List<(Reference, double)>();

        var measure = measureDirection.Normalize();
        var fromFamily = TryGetJambsFromFamilyReferences(instance, measure);
        if (fromFamily.Count >= 2)
            return fromFamily;

        // Instance GetInstanceGeometry usually drops Face.Reference — use symbol geom + transform.
        var candidates = CollectAlignedFaceCutPointsFromInstance(
            instance, view, measure, minAlignment);
        if (candidates.Count >= 2)
        {
            candidates.Sort((a, b) => a.PositionMm.CompareTo(b.PositionMm));
            return new List<(Reference, double)>
            {
                candidates[0],
                candidates[candidates.Count - 1]
            };
        }

        if (fromFamily.Count > 0)
            return fromFamily;
        if (candidates.Count > 0)
            return candidates;

        // Last resort: host wall cut faces near computed jamb positions (have stable refs).
        return TryGetJambsFromHostWallCuts(instance, view, measure, minAlignment);
    }

    private static List<(Reference Reference, double PositionMm)> TryGetJambsFromHostWallCuts(
        FamilyInstance instance,
        View view,
        XYZ measure,
        double minAlignment)
    {
        var result = new List<(Reference Reference, double PositionMm)>();
        if (instance.Host is not Wall host || instance.Location is not LocationPoint locationPoint)
            return result;

        var halfWidth = GetOpeningWidthInternalFeet(instance) / 2.0;
        if (halfWidth <= 1e-6)
            return result;

        var centerMm = ProjectPointMm(locationPoint.Point, measure);
        var halfMm = halfWidth * 304.8;
        const double jambTolMm = 120;

        var wallFaces = CollectAlignedFaceCutPoints(host, view, measure, minAlignment);
        foreach (var face in wallFaces)
        {
            if (Math.Abs(face.PositionMm - (centerMm - halfMm)) <= jambTolMm
                || Math.Abs(face.PositionMm - (centerMm + halfMm)) <= jambTolMm)
            {
                result.Add(face);
            }
        }

        if (result.Count < 2)
            return result;

        result.Sort((a, b) => a.PositionMm.CompareTo(b.PositionMm));
        return new List<(Reference Reference, double PositionMm)>
        {
            result[0],
            result[result.Count - 1]
        };
    }

    private static List<(Reference Reference, double PositionMm)> TryGetJambsFromFamilyReferences(
        FamilyInstance instance,
        XYZ measure)
    {
        var result = new List<(Reference Reference, double PositionMm)>();
        if (instance.Location is not LocationPoint locationPoint)
            return result;

        var loc = locationPoint.Point;
        var hand = instance.HandOrientation;
        if (hand == null || hand.GetLength() < 1e-9)
            return result;

        hand = hand.Normalize();
        // Align hand with measure axis so Left/Right map to min/max along the facade.
        if (hand.DotProduct(measure) < 0)
            hand = hand.Negate();

        var halfWidth = GetOpeningWidthInternalFeet(instance) / 2.0;
        if (halfWidth <= 1e-6)
            return result;

        TryAddFamilyJamb(
            instance,
            FamilyInstanceReferenceType.Left,
            ProjectPointMm(loc - hand * halfWidth, measure),
            result);
        TryAddFamilyJamb(
            instance,
            FamilyInstanceReferenceType.Right,
            ProjectPointMm(loc + hand * halfWidth, measure),
            result);

        if (result.Count >= 2)
        {
            result.Sort((a, b) => a.PositionMm.CompareTo(b.PositionMm));
            return result;
        }

        // Some families only expose CenterLeftRight — still useless alone; keep whatever we got.
        return result;
    }

    private static void TryAddFamilyJamb(
        FamilyInstance instance,
        FamilyInstanceReferenceType referenceType,
        double positionMm,
        List<(Reference Reference, double PositionMm)> sink)
    {
        IList<Reference> refs;
        try
        {
            refs = instance.GetReferences(referenceType);
        }
        catch
        {
            return;
        }

        if (refs == null || refs.Count == 0 || refs[0] == null)
            return;

        sink.Add((refs[0], positionMm));
    }

    private static double ProjectPointMm(XYZ point, XYZ measure)
    {
        return (point.X * measure.X + point.Y * measure.Y) * 304.8;
    }

    private static double GetOpeningWidthInternalFeet(FamilyInstance instance)
    {
        var widthParam = instance.get_Parameter(BuiltInParameter.DOOR_WIDTH)
            ?? instance.get_Parameter(BuiltInParameter.WINDOW_WIDTH)
            ?? instance.Symbol?.get_Parameter(BuiltInParameter.DOOR_WIDTH)
            ?? instance.Symbol?.get_Parameter(BuiltInParameter.WINDOW_WIDTH)
            ?? instance.Symbol?.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM);

        if (widthParam != null && widthParam.HasValue)
        {
            var value = widthParam.AsDouble();
            if (value > 1e-6)
                return value;
        }

        var bbox = instance.get_BoundingBox(null);
        if (bbox == null)
            return 0;

        var hand = instance.HandOrientation;
        if (hand == null || hand.GetLength() < 1e-9)
            return Math.Max(bbox.Max.X - bbox.Min.X, bbox.Max.Y - bbox.Min.Y);

        hand = hand.Normalize();
        var diag = bbox.Max - bbox.Min;
        return Math.Abs(diag.DotProduct(hand));
    }

    private static List<(Reference Reference, double PositionMm)> CollectAlignedFaceCutPointsFromInstance(
        FamilyInstance instance,
        View view,
        XYZ measure,
        double minAlignment)
    {
        var result = new List<(Reference, double)>();
        var options = new Options
        {
            View = view,
            ComputeReferences = true
        };

        var geometry = instance.get_Geometry(options);
        if (geometry == null)
            return result;

        const double feetToMm = 304.8;

        foreach (var obj in geometry)
        {
            if (obj is Solid solid && solid.Faces.Size > 0)
            {
                AddAlignedFaces(solid, Transform.Identity, measure, minAlignment, feetToMm, result);
                continue;
            }

            if (obj is not GeometryInstance geometryInstance)
                continue;

            // Symbol geometry keeps references; apply instance transform to positions.
            var transform = geometryInstance.Transform;
            GeometryElement symbolGeom;
            try
            {
                symbolGeom = geometryInstance.GetSymbolGeometry();
            }
            catch
            {
                continue;
            }

            if (symbolGeom == null)
                continue;

            foreach (var subObj in symbolGeom)
            {
                if (subObj is Solid subSolid && subSolid.Faces.Size > 0)
                    AddAlignedFaces(subSolid, transform, measure, minAlignment, feetToMm, result);
            }
        }

        return result;
    }

    private static void AddAlignedFaces(
        Solid solid,
        Transform transform,
        XYZ measure,
        double minAlignment,
        double feetToMm,
        List<(Reference Reference, double PositionMm)> sink)
    {
        foreach (Face face in solid.Faces)
        {
            if (face is not PlanarFace planarFace || face.Reference == null)
                continue;

            var normal = transform.OfVector(planarFace.FaceNormal).Normalize();
            if (Math.Abs(normal.Z) > 0.9)
                continue;

            var alignment = Math.Abs(normal.DotProduct(measure));
            if (alignment < minAlignment)
                continue;

            var origin = transform.OfPoint(planarFace.Origin);
            var positionMm = (origin.X * measure.X + origin.Y * measure.Y) * feetToMm;
            sink.Add((face.Reference, positionMm));
        }
    }

    /// <summary>
    ///     Wall end faces (normals along <paramref name="measureDirection"/>), typically two.
    ///     Prefer <see cref="GetExteriorShellFaceReference"/> for exterior facade chains —
    ///     joined wall ends often sit on the <em>interior</em> face of the return wall.
    /// </summary>
    public static List<(Reference Reference, double PositionMm)> GetWallEndReferences(
        Wall wall,
        View view,
        XYZ measureDirection,
        double minAlignment = 0.85)
    {
        var candidates = CollectAlignedFaceCutPoints(wall, view, measureDirection, minAlignment);
        if (candidates.Count == 0)
            return new List<(Reference Reference, double PositionMm)>();

        candidates.Sort((a, b) => a.PositionMm.CompareTo(b.PositionMm));
        if (candidates.Count == 1)
            return candidates;

        return new List<(Reference Reference, double PositionMm)>
        {
            candidates[0],
            candidates[candidates.Count - 1]
        };
    }

    /// <summary>
    ///     Exterior finish face of a wall whose normal aligns with
    ///     <paramref name="outwardNormal"/> (unit vector pointing outside the building).
    ///     Used for exterior dimension chains so corners snap to outer envelope, not inner.
    /// </summary>
    public static (Reference Reference, double PositionMm)? GetExteriorShellFaceReference(
        Wall wall,
        XYZ outwardNormal,
        XYZ measureDirection,
        double minAlignment = 0.85)
    {
        if (wall == null || outwardNormal == null || outwardNormal.GetLength() < 1e-9)
            return null;

        var outward = outwardNormal.Normalize();
        var measure = measureDirection?.Normalize() ?? outward;

        Reference bestRef = null;
        var bestScore = double.MinValue;
        var bestPos = 0.0;

        foreach (var reference in EnumerateWallSideFaceReferences(wall, ShellLayerType.Exterior))
        {
            if (wall.GetGeometryObjectFromReference(reference) is not PlanarFace planarFace)
                continue;

            var normal = planarFace.FaceNormal;
            if (Math.Abs(normal.Z) > 0.9)
                continue;

            var outwardDot = normal.DotProduct(outward);
            if (outwardDot < minAlignment)
                continue;

            var measureDot = Math.Abs(normal.DotProduct(measure));
            if (measureDot < minAlignment)
                continue;

            var origin = planarFace.Origin;
            var positionMm = (origin.X * measure.X + origin.Y * measure.Y) * 304.8;
            // Prefer faces that point most strongly outward.
            if (outwardDot > bestScore)
            {
                bestScore = outwardDot;
                bestRef = reference;
                bestPos = positionMm;
            }
        }

        if (bestRef != null)
            return (bestRef, bestPos);

        // Fallback: any side face pointing outward (some compound walls omit Exterior).
        foreach (var reference in EnumerateWallSideFaceReferences(wall))
        {
            if (wall.GetGeometryObjectFromReference(reference) is not PlanarFace planarFace)
                continue;

            var normal = planarFace.FaceNormal;
            if (Math.Abs(normal.Z) > 0.9)
                continue;

            var outwardDot = normal.DotProduct(outward);
            if (outwardDot < minAlignment)
                continue;

            var origin = planarFace.Origin;
            var positionMm = (origin.X * measure.X + origin.Y * measure.Y) * 304.8;
            if (outwardDot > bestScore)
            {
                bestScore = outwardDot;
                bestRef = reference;
                bestPos = positionMm;
            }
        }

        return bestRef == null ? null : (bestRef, bestPos);
    }

    /// <summary>
    ///     Interior finish face closest to <paramref name="preferNearPoint"/> (room center).
    ///     Alias kept for callers; same as <see cref="FindRoomSideWallFace"/>.
    /// </summary>
    public static Reference GetInteriorShellFaceReference(
        Wall wall,
        View view,
        XYZ measureDirection,
        XYZ preferNearPoint)
    {
        return FindRoomSideWallFace(wall, view, measureDirection, preferNearPoint);
    }

    private static IEnumerable<Reference> EnumerateWallSideFaceReferences(
        Wall wall,
        ShellLayerType layer)
    {
        IList<Reference> faces;
        try
        {
            faces = HostObjectUtils.GetSideFaces(wall, layer);
        }
        catch
        {
            yield break;
        }

        if (faces == null)
            yield break;

        foreach (var face in faces)
        {
            if (face != null)
                yield return face;
        }
    }

    private static List<(Reference Reference, double PositionMm)> CollectAlignedFaceCutPoints(
        Element element,
        View view,
        XYZ measureDirection,
        double minAlignment)
    {
        var result = new List<(Reference Reference, double PositionMm)>();
        if (element == null || measureDirection == null || measureDirection.GetLength() < 1e-9)
            return result;

        var measure = measureDirection.Normalize();
        var options = new Options
        {
            View = view,
            ComputeReferences = true
        };

        var geometry = element.get_Geometry(options);
        if (geometry == null)
            return result;

        const double feetToMm = 304.8;

        foreach (var obj in geometry)
        {
            foreach (var solid in EnumerateSolids(obj))
            {
                foreach (Face face in solid.Faces)
                {
                    if (face is not PlanarFace planarFace || face.Reference == null)
                        continue;

                    var normal = planarFace.FaceNormal;
                    if (Math.Abs(normal.Z) > 0.9)
                        continue;

                    var alignment = Math.Abs(normal.DotProduct(measure));
                    if (alignment < minAlignment)
                        continue;

                    var origin = planarFace.Origin;
                    var positionMm = (origin.X * measure.X + origin.Y * measure.Y) * feetToMm;
                    result.Add((face.Reference, positionMm));
                }
            }
        }

        return result;
    }

    private static IEnumerable<Solid> EnumerateSolids(GeometryObject obj)
    {
        if (obj is Solid solid && solid.Faces.Size > 0)
        {
            yield return solid;
            yield break;
        }

        if (obj is not GeometryInstance geometryInstance)
            yield break;

        foreach (var subObj in geometryInstance.GetInstanceGeometry())
        {
            if (subObj is Solid subSolid && subSolid.Faces.Size > 0)
                yield return subSolid;
        }
    }

    private static XYZ ComputeDefaultLineAnchor(XYZ startPoint, XYZ endPoint, XYZ direction, double offsetMm)
    {
        var midpoint = (startPoint + endPoint) / 2.0;
        var perpendicular = new XYZ(-direction.Y, direction.X, 0);
        if (perpendicular.GetLength() < 1e-9)
            perpendicular = XYZ.BasisY;
        else
            perpendicular = perpendicular.Normalize();

        return midpoint + perpendicular * (ResolveOffsetMm(offsetMm) * MillimetersToFeet);
    }
}
