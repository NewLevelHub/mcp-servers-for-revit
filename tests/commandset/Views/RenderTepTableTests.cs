using System.Globalization;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPCommandSet.Services.Views;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Views;

public class RenderTepTableTests : RevitApiTest
{
    private static Document _doc;
    private static Level _level;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Render TEP Table Test");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "TEP Render Level";

        CreateEnclosedRoom(_doc, _level, 10.0, new UV(5.0, 5.0), "Living", "Жилые");
        CreateEnclosedRoom(_doc, _level, 8.0, new UV(20.0, 5.0), "Office", "Общественные");

        var reference = ViewSchedule.CreateSchedule(_doc, new ElementId(BuiltInCategory.OST_Rooms));
        reference.Name = "MCP TEP Reference";
        AddReferenceField(reference, "Number", "№ п/п", 15.0, ScheduleHorizontalAlignment.Center);
        AddReferenceField(reference, "Name", "Наименование", 80.0, ScheduleHorizontalAlignment.Left);
        AddReferenceField(reference, "Level", "Ед. изм.", 20.0, ScheduleHorizontalAlignment.Center);
        AddReferenceField(reference, "Area", "Кол-во", 30.0, ScheduleHorizontalAlignment.Center);

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task RenderTepTable_DefaultColumns_CreatesSheetTextAndGrid()
    {
        var result = RenderTepTableEventHandler.Render(_doc, new TepTableRenderInfo
        {
            SheetName = "Общие данные",
            SheetNumber = "ОД-1"
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.SheetCreated).IsTrue();
        await Assert.That(result.SheetName).IsEqualTo("Общие данные");
        await Assert.That(result.Columns.Count).IsEqualTo(4);
        await Assert.That(result.Columns[0].Heading).IsEqualTo("№ п/п");
        // Columns grow to fit the longest cell (Revit TextNote min width + content).
        await Assert.That(result.Columns[1].Width).IsGreaterThanOrEqualTo(90.0);
        await Assert.That(result.RowCount).IsGreaterThanOrEqualTo(5);
        await Assert.That(result.BodyTextType).IsNotEmpty();

        var sheet = (ViewSheet)_doc.GetElement(result.SheetUniqueId);
        await Assert.That(sheet).IsNotNull();

        var textNotes = new FilteredElementCollector(_doc, sheet.Id)
            .OfClass(typeof(TextNote))
            .Cast<TextNote>()
            .ToList();
        await Assert.That(textNotes.Count).IsEqualTo(result.TextNoteIds.Count);

        // Grid: (rows + header + 1) horizontal + (columns + 1) vertical lines
        var expectedLineCount = result.RowCount + 2 + result.Columns.Count + 1;
        await Assert.That(result.DetailLineIds.Count).IsEqualTo(expectedLineCount);

        var tepData = ExportTepDataEventHandler.Compute(_doc);
        var expectedTotalArea = Math.Round(tepData.TotalArea, 2, MidpointRounding.AwayFromZero)
            .ToString("0.##", CultureInfo.InvariantCulture);
        await Assert.That(textNotes.Any(note => note.Text.Trim() == expectedTotalArea)).IsTrue();
        await Assert.That(textNotes.Any(note => note.Text.Contains("Площадь застройки"))).IsTrue();
        await Assert.That(textNotes.Any(note => note.Text.Contains("Жилые"))).IsTrue();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task RenderTepTable_TemplateSchedule_ReplicatesColumnsAndMapsRoles()
    {
        var result = RenderTepTableEventHandler.Render(_doc, new TepTableRenderInfo
        {
            TemplateScheduleName = "MCP TEP Reference",
            SheetName = "Общие данные ТЭП (эталон)"
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.TemplateScheduleUsed).IsTrue();
        await Assert.That(result.Columns.Count).IsEqualTo(4);
        await Assert.That(result.Columns[1].Heading).IsEqualTo("Наименование");
        await Assert.That(result.Columns[1].Width).IsGreaterThanOrEqualTo(80.0);
        await Assert.That(result.Columns[0].Role).IsEqualTo("Index");
        await Assert.That(result.Columns[1].Role).IsEqualTo("Name");
        await Assert.That(result.Columns[2].Role).IsEqualTo("Unit");
        await Assert.That(result.Columns[3].Role).IsEqualTo("Value");
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task RenderTepTable_ExistingSheet_IsReused()
    {
        using (var tx = new Transaction(_doc, "Create existing sheet"))
        {
            tx.Start();
            var sheet = ViewSheet.Create(_doc, ElementId.InvalidElementId);
            sheet.Name = "Общие данные (существующий)";
            tx.Commit();
        }

        var result = RenderTepTableEventHandler.Render(_doc, new TepTableRenderInfo
        {
            SheetName = "Общие данные (существующий)"
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.SheetCreated).IsFalse();
        await Assert.That(result.SheetName).IsEqualTo("Общие данные (существующий)");
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task RenderTepTable_MissingSheetWithoutCreate_Throws()
    {
        var thrown = false;
        try
        {
            RenderTepTableEventHandler.Render(_doc, new TepTableRenderInfo
            {
                SheetName = "Лист, которого нет",
                CreateSheetIfMissing = false
            });
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }

        await Assert.That(thrown).IsTrue();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task RenderTepTable_MissingTemplate_FallsBackToDefaultColumnsWithWarning()
    {
        var result = RenderTepTableEventHandler.Render(_doc, new TepTableRenderInfo
        {
            TemplateScheduleName = "Спецификация, которой нет",
            SheetName = "Общие данные (fallback)"
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.TemplateScheduleUsed).IsFalse();
        await Assert.That(result.Columns.Count).IsEqualTo(4);
        await Assert.That(result.Warnings.Any(warning => warning.Contains("was not found"))).IsTrue();
    }

    private static void AddReferenceField(
        ViewSchedule schedule,
        string parameterName,
        string heading,
        double widthMm,
        ScheduleHorizontalAlignment alignment)
    {
        var definition = schedule.Definition;
        foreach (var schedulableField in definition.GetSchedulableFields())
        {
            if (!schedulableField.GetName(schedule.Document)
                    .Equals(parameterName, StringComparison.OrdinalIgnoreCase))
                continue;

            var field = definition.AddField(schedulableField);
            field.ColumnHeading = heading;
            field.GridColumnWidth = widthMm / 304.8;
            field.HorizontalAlignment = alignment;
            return;
        }
    }

    private static void CreateEnclosedRoom(
        Document doc,
        Level level,
        double sizeFeet,
        UV location,
        string roomName,
        string department)
    {
        double z = level.Elevation;
        var p1 = new XYZ(location.U, location.V, z);
        var p2 = new XYZ(location.U + sizeFeet, location.V, z);
        var p3 = new XYZ(location.U + sizeFeet, location.V + sizeFeet, z);
        var p4 = new XYZ(location.U, location.V + sizeFeet, z);

        Wall.Create(doc, Line.CreateBound(p1, p2), level.Id, false);
        Wall.Create(doc, Line.CreateBound(p2, p3), level.Id, false);
        Wall.Create(doc, Line.CreateBound(p3, p4), level.Id, false);
        Wall.Create(doc, Line.CreateBound(p4, p1), level.Id, false);

        var room = doc.Create.NewRoom(level, new UV(location.U + sizeFeet / 2.0, location.V + sizeFeet / 2.0));
        if (room == null)
            return;

        room.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set(roomName);
        room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.Set(department);
    }
}
