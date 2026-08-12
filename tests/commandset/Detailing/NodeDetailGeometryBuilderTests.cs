using RevitMCPCommandSet.Utils.Detailing;
using TUnit.Core;

namespace RevitMCPCommandSet.Tests.Detailing;

/// <summary>
///     Pure geometry of a generated node — no Revit document, so these run in CI.
/// </summary>
public class NodeDetailGeometryBuilderTests
{
    private static NodeLayerSpec Layer(string name, double thickness, string function, bool core = false)
    {
        return new NodeLayerSpec
        {
            Name = name,
            ThicknessMm = thickness,
            Function = function,
            IsCore = core
        };
    }

    private static List<NodeLayerSpec> Floor()
    {
        return new List<NodeLayerSpec>
        {
            Layer("Ламинат", 10, "Finish1"),
            Layer("Стяжка", 60, "Substrate"),
            Layer("Плита перекрытия", 200, "Structure", core: true)
        };
    }

    private static List<NodeLayerSpec> Wall()
    {
        return new List<NodeLayerSpec>
        {
            Layer("Штукатурка", 20, "Finish2"),
            Layer("Кирпич", 250, "Structure", core: true),
            Layer("Штукатурка", 20, "Finish1")
        };
    }

    [Test]
    public async Task Single_Horizontal_StacksLayersDownwardsFromFinishedLevel()
    {
        var geometry = NodeDetailGeometryBuilder.Build(new NodeDetailRequest
        {
            Mode = "single",
            Orientation = "horizontal",
            PrimaryLayers = Floor(),
            LengthMm = 600,
            Scale = 10
        });

        await Assert.That(geometry.Contours.Count).IsEqualTo(3);

        // Finished floor level is y = 0 and the build-up hangs below it.
        await Assert.That(geometry.Contours[0].Points.Max(point => point.Y)).IsEqualTo(0);
        await Assert.That(geometry.Bounds.MinY).IsEqualTo(-270);
        await Assert.That(geometry.Contours[2].Points.Min(point => point.Y)).IsEqualTo(-270);
    }

    [Test]
    public async Task Single_Vertical_StacksLayersLeftToRight()
    {
        var geometry = NodeDetailGeometryBuilder.Build(new NodeDetailRequest
        {
            Mode = "single",
            Orientation = "vertical",
            PrimaryLayers = Wall(),
            LengthMm = 400,
            Scale = 10
        });

        await Assert.That(geometry.Contours.Count).IsEqualTo(3);
        await Assert.That(geometry.Contours[0].Points.Min(point => point.X)).IsEqualTo(0);
        await Assert.That(geometry.Contours[2].Points.Max(point => point.X)).IsEqualTo(290);
    }

    [Test]
    public async Task Junction_HoldsFinishLayersOffTheWallByTheGap()
    {
        var geometry = NodeDetailGeometryBuilder.Build(new NodeDetailRequest
        {
            Mode = "junction",
            PrimaryLayers = Wall(),
            SecondaryLayers = Floor(),
            LengthMm = 600,
            GapMm = 20,
            Scale = 10
        });

        // Wall is 290 thick, so the floor starts at x = 290; laminate and screed stop 20 mm short.
        var laminate = geometry.Contours.First(contour => contour.Label.StartsWith("Ламинат"));
        var slab = geometry.Contours.First(contour => contour.Label.StartsWith("Плита"));

        await Assert.That(laminate.Points.Min(point => point.X)).IsEqualTo(310);
        await Assert.That(slab.Points.Min(point => point.X)).IsEqualTo(290);

        // The gap face is one line over the whole inset run (laminate 10 + screed 60), not a
        // piece per layer with a separate gap line laid on top of them.
        var gap = geometry.Segments.Where(segment => segment.Role == "gap").ToList();
        await Assert.That(gap.Count).IsEqualTo(1);
        await Assert.That(gap[0].Start.X).IsEqualTo(310);
        await Assert.That(Math.Abs(gap[0].Start.Y - gap[0].End.Y)).IsEqualTo(70);

        await Assert.That(geometry.Segments.Select(Describe).Distinct().Count())
            .IsEqualTo(geometry.Segments.Count);
    }

    [Test]
    public async Task Junction_WithoutFinishesAboveCore_WarnsInsteadOfDrawingNoGap()
    {
        var geometry = NodeDetailGeometryBuilder.Build(new NodeDetailRequest
        {
            Mode = "junction",
            PrimaryLayers = Wall(),
            SecondaryLayers = new List<NodeLayerSpec>
            {
                Layer("Плита перекрытия", 200, "Structure", core: true)
            },
            GapMm = 20,
            Scale = 10
        });

        await Assert.That(geometry.Warnings.Count).IsEqualTo(1);
        await Assert.That(geometry.Warnings[0]).Contains("expansion gap");
        await Assert.That(geometry.Segments.Any(segment => segment.Role == "gap")).IsFalse();
    }

    [Test]
    public async Task Junction_DimensionChainsReferenceEveryLayerBoundary()
    {
        var geometry = NodeDetailGeometryBuilder.Build(new NodeDetailRequest
        {
            Mode = "junction",
            PrimaryLayers = Wall(),
            SecondaryLayers = Floor(),
            Scale = 10
        });

        await Assert.That(geometry.Dimensions.Count).IsEqualTo(2);

        // Three layers give four boundaries in each chain.
        foreach (var dimension in geometry.Dimensions)
            await Assert.That(dimension.SegmentIndices.Count).IsEqualTo(4);
    }

    [Test]
    public async Task ZeroWidthLayer_DrawnAsLineAndNotDimensionedTwice()
    {
        var layers = new List<NodeLayerSpec>
        {
            Layer("Стяжка", 60, "Substrate"),
            Layer("Гидроизоляция", 0, "Membrane"),
            Layer("Плита", 200, "Structure", core: true)
        };

        var geometry = NodeDetailGeometryBuilder.Build(new NodeDetailRequest
        {
            Mode = "single",
            Orientation = "horizontal",
            PrimaryLayers = layers,
            Scale = 10
        });

        // The membrane gets no filled area, only the boundary line it already shares.
        await Assert.That(geometry.Contours.Count).IsEqualTo(2);

        // Four boundaries, but two of them coincide — a zero-length dimension is not placed.
        await Assert.That(geometry.Dimensions[0].SegmentIndices.Count).IsEqualTo(3);
        await Assert.That(geometry.Notes.Count).IsEqualTo(3);

        // The shared boundary gets one line, not two identical curves stacked in the view.
        var divisions = geometry.Segments.Where(segment => segment.Role == "division").ToList();
        await Assert.That(divisions.Count).IsEqualTo(3);
        await Assert.That(divisions.Select(Describe).Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task ZeroWidthLayer_SharedDivisionLineSpansTheWiderNeighbour()
    {
        var geometry = NodeDetailGeometryBuilder.Build(new NodeDetailRequest
        {
            Mode = "junction",
            PrimaryLayers = Wall(),
            SecondaryLayers = new List<NodeLayerSpec>
            {
                Layer("Ламинат", 10, "Finish1"),
                Layer("Гидроизоляция", 0, "Membrane"),
                Layer("Плита перекрытия", 200, "Structure", core: true)
            },
            GapMm = 20,
            Scale = 10
        });

        // The wall is 290 thick. Laminate and membrane are held 20 mm off it, the slab is not,
        // so the line they share has to reach all the way back to the slab or the dimension
        // chain attaches to a curve that stops short.
        var shared = geometry.Segments
            .Where(segment => segment.Role == "division" && Math.Abs(segment.Start.Y + 10) < 0.001)
            .ToList();

        await Assert.That(shared.Count).IsEqualTo(1);
        await Assert.That(shared[0].Start.X).IsEqualTo(290);
    }

    [Test]
    public async Task WallLabels_StackAsAColumnInsteadOfAlongTheLayers()
    {
        var geometry = NodeDetailGeometryBuilder.Build(new NodeDetailRequest
        {
            Mode = "junction",
            Scale = 10,
            GapMm = 20,
            LengthMm = 500,
            PrimaryLayers = new List<NodeLayerSpec>
            {
                Layer("Обшивка", 15, "Finish1"),
                Layer("ADSK_Обшивка_Гипсокартон обычный", 125, "Substrate")
            },
            SecondaryLayers = Floor()
        });

        // The wall stacks sideways. Dealing its labels out along that axis spaced them by 60 mm
        // while the text itself ran 800 mm, so every label landed on the one before it.
        var wallNotes = geometry.Notes.Take(2).ToList();

        await Assert.That(wallNotes[0].Position.X).IsEqualTo(wallNotes[1].Position.X);
        await Assert.That(Math.Abs(wallNotes[0].Position.Y - wallNotes[1].Position.Y)).IsEqualTo(60);

        // A column left of the drawing has to grow leftwards.
        await Assert.That(wallNotes.All(note => note.AlignRight)).IsTrue();

        // Each leader still reaches the middle of its own layer.
        await Assert.That(wallNotes[0].LeaderEnd.X).IsEqualTo(7.5);
        await Assert.That(wallNotes[1].LeaderEnd.X).IsEqualTo(77.5);

        // The floor stacks downwards already, so its labels keep sitting beside their layers,
        // in a column on the other side of the node.
        var floorNotes = geometry.Notes.Skip(2).ToList();
        await Assert.That(floorNotes.Select(note => note.Position.X).Distinct().Count()).IsEqualTo(1);
        await Assert.That(floorNotes.Any(note => note.AlignRight)).IsFalse();
    }

    private static string Describe(NodeSegment segment) =>
        $"{segment.Start.X};{segment.Start.Y};{segment.End.X};{segment.End.Y}";

    [Test]
    public async Task AnnotationOffsetsFollowTheViewScale()
    {
        NodeDetailGeometry AtScale(int scale) => NodeDetailGeometryBuilder.Build(new NodeDetailRequest
        {
            Mode = "single",
            Orientation = "horizontal",
            PrimaryLayers = Floor(),
            LengthMm = 600,
            Scale = scale
        });

        var atTen = AtScale(10);
        var atTwenty = AtScale(20);

        // Notes sit 26 paper mm out, so doubling the scale doubles the model-space offset.
        await Assert.That(atTen.Notes[0].Position.X).IsEqualTo(600 + 260);
        await Assert.That(atTwenty.Notes[0].Position.X).IsEqualTo(600 + 520);
    }

    [Test]
    public async Task SpreadRows_PushesLabelsApartWhenLayersAreThin()
    {
        // Three layers 5 mm apart cannot each hold a label at its own midpoint.
        var rows = NodeDetailGeometryBuilder.SpreadRows(new[] { 0.0, -5.0, -10.0 }, 60);

        await Assert.That(rows[0]).IsEqualTo(0);
        await Assert.That(rows[1]).IsEqualTo(-60);
        await Assert.That(rows[2]).IsEqualTo(-120);
    }

    [Test]
    public async Task SpreadRows_LeavesWellSpacedLabelsWhereTheyAre()
    {
        var rows = NodeDetailGeometryBuilder.SpreadRows(new[] { 0.0, -200.0, -400.0 }, 60);

        await Assert.That(rows).IsEquivalentTo(new List<double> { 0, -200, -400 });
    }

    [Test]
    public async Task Junction_WithoutSecondAssembly_FailsInsteadOfDrawingHalfANode()
    {
        await Assert.That(() => NodeDetailGeometryBuilder.Build(new NodeDetailRequest
        {
            Mode = "junction",
            PrimaryLayers = Wall(),
            SecondaryLayers = new List<NodeLayerSpec>()
        })).Throws<ArgumentException>();
    }

    [Test]
    public async Task UnknownMode_Fails()
    {
        await Assert.That(() => NodeDetailGeometryBuilder.Build(new NodeDetailRequest
        {
            Mode = "isometric",
            PrimaryLayers = Wall()
        })).Throws<ArgumentException>();
    }
}
