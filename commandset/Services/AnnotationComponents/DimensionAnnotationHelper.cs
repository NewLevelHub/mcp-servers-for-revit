using Autodesk.Revit.DB;
using RevitMCPCommandSet.Models.Common;

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

        if (closestElement == null || minDistance > toleranceFeet)
            return null;

        var refs = GetReferences(closestElement, view, dimensionDirection);
        return refs.Count > 0 ? refs[0] : null;
    }

    /// <summary>
    ///     Prefer the wall face on the room side (inner finish face): among vertical planar
    ///     faces aligned with <paramref name="measureDirection"/>, pick the one closest to
    ///     <paramref name="preferNearPoint"/> (typically the room center). This keeps clear
    ///     room dimensions from starting outside or through the wall thickness.
    /// </summary>
    public static Reference GetBestWallReference(
        Wall wall,
        View view,
        XYZ measureDirection,
        XYZ preferNearPoint)
    {
        var roomSide = FindRoomSideWallFace(wall, view, measureDirection, preferNearPoint);
        if (roomSide != null)
            return roomSide;

        var refs = GetReferences(wall, view, measureDirection);
        return refs.Count > 0 ? refs[0] : null;
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
            IList<Reference> faces;
            try
            {
                faces = HostObjectUtils.GetSideFaces(wall, layer);
            }
            catch
            {
                continue;
            }

            if (faces == null)
                continue;

            foreach (var face in faces)
            {
                if (face != null)
                    yield return face;
            }
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
