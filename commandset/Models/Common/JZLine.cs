using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Common;

/// <summary>
///     三维线段
/// </summary>
public class JZLine
{
    /// <summary>
    ///     构造函数
    /// </summary>
    public JZLine()
    {
    }

    /// <summary>
    ///     构造函数
    /// </summary>
    public JZLine(JZPoint p0, JZPoint p1)
    {
        P0 = p0;
        P1 = p1;
    }

    /// <summary>
    ///     四个double作为参数的构造函数
    /// </summary>
    /// <param name="x0">起点X坐标</param>
    /// <param name="y0">起点Y坐标</param>
    /// <param name="z0">起点Z坐标</param>
    /// <param name="x1">终点X坐标</param>
    /// <param name="y1">终点Y坐标</param>
    /// <param name="z1">终点Z坐标</param>
    public JZLine(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        P0 = new JZPoint(x0, y0, z0);
        P1 = new JZPoint(x1, y1, z1);
    }

    /// <summary>
    ///     四个double作为参数的构造函数
    /// </summary>
    /// <param name="x0">起点X坐标</param>
    /// <param name="y0">起点Y坐标</param>
    /// <param name="z0">起点Z坐标</param>
    /// <param name="x1">终点X坐标</param>
    /// <param name="y1">终点Y坐标</param>
    /// <param name="z1">终点Z坐标</param>
    public JZLine(double x0, double y0, double x1, double y1)
    {
        P0 = new JZPoint(x0, y0, 0);
        P1 = new JZPoint(x1, y1, 0);
    }

    /// <summary>
    ///     起点
    /// </summary>
    [JsonProperty("p0")]
    public JZPoint P0 { get; set; }

    /// <summary>
    ///     终点
    /// </summary>
    [JsonProperty("p1")]
    public JZPoint P1 { get; set; }

    /// <summary>
    ///     Optional third point the location curve has to pass through (mm).
    ///     Set by the CAD tracer for curved walls: a DWG arc arrives tessellated into chords,
    ///     and Revit needs three points to rebuild the original arc (REV-154).
    /// </summary>
    [JsonProperty("pointOnCurve")]
    public JZPoint PointOnCurve { get; set; }

    /// <summary>
    ///     获取线段的长度
    /// </summary>
    public double GetLength()
    {
        if (P0 == null || P1 == null)
            throw new InvalidOperationException("JZLine must have both P0 and P1 defined to calculate length.");

        // 计算三维点之间的距离
        var dx = P1.X - P0.X;
        var dy = P1.Y - P0.Y;
        var dz = P1.Z - P0.Z;

        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    ///     获取线段的方向
    ///     返回一个归一化的 JZPoint 表示方向向量
    /// </summary>
    public JZPoint GetDirection()
    {
        if (P0 == null || P1 == null)
            throw new InvalidOperationException("JZLine must have both P0 and P1 defined to calculate direction.");

        // 计算方向向量
        var dx = P1.X - P0.X;
        var dy = P1.Y - P0.Y;
        var dz = P1.Z - P0.Z;

        // 计算向量的模
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (length == 0)
            throw new InvalidOperationException("Cannot determine direction for a line with zero length.");

        // 返回归一化向量
        return new JZPoint(dx / length, dy / length, dz / length);
    }

    /// <summary>
    ///     转换为Revit的Line
    ///     单位转换：mm -> ft
    /// </summary>
    public static Line ToLine(JZLine jzLine)
    {
        if (jzLine == null)
            throw new ArgumentNullException(nameof(jzLine), "locationLine is required (p0 and p1 in mm).");
        if (jzLine.P0 == null || jzLine.P1 == null)
            throw new ArgumentException("locationLine.p0 and locationLine.p1 are required (mm).");

        var p0 = JZPoint.ToXYZ(jzLine.P0);
        var p1 = JZPoint.ToXYZ(jzLine.P1);
        if (p0.DistanceTo(p1) < 1e-6)
            throw new ArgumentException("locationLine length is zero — p0 and p1 must differ.");

        return Line.CreateBound(p0, p1);
    }

    /// <summary>
    ///     转换为Revit的Curve：有 pointOnCurve 时得到圆弧，否则直线
    ///     单位转换：mm -> ft
    /// </summary>
    public static Curve ToCurve(JZLine jzLine)
    {
        var line = ToLine(jzLine);
        if (jzLine.PointOnCurve == null)
            return line;

        var p0 = JZPoint.ToXYZ(jzLine.P0);
        var p1 = JZPoint.ToXYZ(jzLine.P1);
        var onArc = JZPoint.ToXYZ(jzLine.PointOnCurve);

        // A point that sits on the chord makes Arc.Create throw. Traced arcs get flat when the
        // DWG sweep is tiny, so degrade to the straight segment instead of failing the batch.
        var chord = p1 - p0;
        var sagitta = (onArc - p0).CrossProduct(chord.Normalize()).GetLength();
        if (sagitta < 1.0 / 304.8)
            return line;

        return Arc.Create(p0, p1, onArc);
    }
}