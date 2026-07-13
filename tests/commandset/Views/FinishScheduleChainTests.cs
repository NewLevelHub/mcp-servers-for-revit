using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Services.Views;
using RevitMCPCommandSet.Utils;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Views;

public class FinishScheduleChainTests : RevitApiTest
{
    private const string KeyTemplateName = "В_Отделка-помещения-01_Стили_Ключевая";
    private const string DataTemplateName = "В_Отделка-помещения-02_Заполнение данных";
    private const string OutputByNameTemplateName = "О_АР_Ведомость отделки помещений_Имя";
    private const string OutputByNumberTemplateName = "О_АР_Ведомость отделки помещений_Номер";

    private static Document _doc;
    private static Level _level;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Finish Schedule Chain Test");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "Finish Chain Level";

        CreateEnclosedRoom(_doc, _level, 10.0, new UV(5.0, 5.0), "Living", "101");

        var roomsCategoryId = new ElementId(BuiltInCategory.OST_Rooms);

        var keySchedule = ViewSchedule.CreateKeySchedule(_doc, roomsCategoryId);
        keySchedule.Name = KeyTemplateName;

        var dataSchedule = ViewSchedule.CreateSchedule(_doc, roomsCategoryId);
        dataSchedule.Name = DataTemplateName;
        AddField(dataSchedule, "Number");
        AddField(dataSchedule, "Name");

        var outputByName = ViewSchedule.CreateSchedule(_doc, roomsCategoryId);
        outputByName.Name = OutputByNameTemplateName;
        AddField(outputByName, "Name");
        AddField(outputByName, "Floor Finish");
        AddField(outputByName, "Wall Finish");

        var outputByNumber = ViewSchedule.CreateSchedule(_doc, roomsCategoryId);
        outputByNumber.Name = OutputByNumberTemplateName;
        AddField(outputByNumber, "Number");
        AddField(outputByNumber, "Floor Finish");

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
    public async Task DiscoverChainTemplates_AdskNaming_FindsKeyDataAndOutputsInOrder()
    {
        var warnings = new List<string>();
        var chain = CreateFinishScheduleEventHandler.DiscoverChainTemplates(
            _doc,
            new FinishScheduleCreationInfo(),
            warnings);

        await Assert.That(chain.Count).IsEqualTo(4);
        await Assert.That(chain[0].Role).IsEqualTo(CreateFinishScheduleEventHandler.RoleKey);
        await Assert.That(chain[0].Schedule.Name).IsEqualTo(KeyTemplateName);
        await Assert.That(chain[0].Schedule.Definition.IsKeySchedule).IsTrue();
        await Assert.That(chain[1].Role).IsEqualTo(CreateFinishScheduleEventHandler.RoleData);
        await Assert.That(chain[1].Schedule.Name).IsEqualTo(DataTemplateName);
        await Assert.That(chain[2].Role).IsEqualTo(CreateFinishScheduleEventHandler.RoleOutput);
        await Assert.That(chain[3].Role).IsEqualTo(CreateFinishScheduleEventHandler.RoleOutput);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task DiscoverChainTemplates_ExplicitNames_ResolvesRequestedSchedules()
    {
        var warnings = new List<string>();
        var chain = CreateFinishScheduleEventHandler.DiscoverChainTemplates(
            _doc,
            new FinishScheduleCreationInfo
            {
                KeyScheduleTemplateName = KeyTemplateName,
                DataScheduleTemplateName = DataTemplateName,
                OutputScheduleTemplateNames = new List<string> { OutputByNameTemplateName }
            },
            warnings);

        await Assert.That(chain.Count).IsEqualTo(3);
        await Assert.That(chain[2].Schedule.Name).IsEqualTo(OutputByNameTemplateName);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task ReproduceChain_Default_ReusesKeyAndDuplicatesDataAndOutputs()
    {
        var warnings = new List<string>();
        var info = new FinishScheduleCreationInfo { Name = "Ведомость отделки (тест)" };
        var templates = CreateFinishScheduleEventHandler.DiscoverChainTemplates(_doc, info, warnings);

        var items = CreateFinishScheduleEventHandler.ReproduceChain(_doc, templates, info, warnings);

        await Assert.That(items.Count).IsEqualTo(4);

        var keyItem = items.First(item => item.Role == CreateFinishScheduleEventHandler.RoleKey);
        await Assert.That(keyItem.ReusedExisting).IsTrue();
        await Assert.That(keyItem.ScheduleName).IsEqualTo(KeyTemplateName);

        var dataItem = items.First(item => item.Role == CreateFinishScheduleEventHandler.RoleData);
        await Assert.That(dataItem.ReusedExisting).IsFalse();
        await Assert.That(dataItem.ScheduleName).IsEqualTo("Ведомость отделки (тест)_02_Заполнение данных");

        var dataDuplicate = _doc.GetElement(dataItem.ScheduleUniqueId) as ViewSchedule;
        var dataTemplate = _doc.GetElement(ElementIdExtensions.FromLong(dataItem.TemplateId)) as ViewSchedule;
        await Assert.That(dataDuplicate).IsNotNull();
        await Assert.That(dataDuplicate!.Definition.IsKeySchedule).IsFalse();
        await Assert.That(dataDuplicate.Definition.GetFieldCount())
            .IsEqualTo(dataTemplate!.Definition.GetFieldCount());

        var outputItems = items.Where(item => item.Role == CreateFinishScheduleEventHandler.RoleOutput).ToList();
        await Assert.That(outputItems.Count).IsEqualTo(2);
        await Assert.That(outputItems.All(item => !item.ReusedExisting)).IsTrue();
        await Assert.That(outputItems.Any(item =>
            item.ScheduleName == "Ведомость отделки (тест)_Имя")).IsTrue();
        await Assert.That(outputItems.Any(item =>
            item.ScheduleName == "Ведомость отделки (тест)_Номер")).IsTrue();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task ReproduceChain_DuplicateKeyRequested_DuplicatesOrFallsBackWithWarning()
    {
        var warnings = new List<string>();
        var info = new FinishScheduleCreationInfo
        {
            Name = "Ведомость отделки (ключ)",
            DuplicateKeySchedule = true
        };
        var templates = CreateFinishScheduleEventHandler.DiscoverChainTemplates(_doc, info, warnings);

        var items = CreateFinishScheduleEventHandler.ReproduceChain(_doc, templates, info, warnings);

        var keyItem = items.First(item => item.Role == CreateFinishScheduleEventHandler.RoleKey);
        if (keyItem.ReusedExisting)
        {
            await Assert.That(warnings.Any(warning => warning.Contains("Failed to duplicate"))).IsTrue();
        }
        else
        {
            await Assert.That(keyItem.ScheduleName).Contains("_01_Стили_Ключевая");
            await Assert.That(warnings.Any(warning =>
                warning.Contains("key parameter"))).IsTrue();
        }
    }

    private static void AddField(ViewSchedule schedule, string parameterName)
    {
        var definition = schedule.Definition;
        foreach (var schedulableField in definition.GetSchedulableFields())
        {
            if (schedulableField.GetName(schedule.Document)
                .Equals(parameterName, StringComparison.OrdinalIgnoreCase))
            {
                definition.AddField(schedulableField);
                return;
            }
        }
    }

    private static void CreateEnclosedRoom(
        Document doc,
        Level level,
        double sizeFeet,
        UV location,
        string roomName,
        string roomNumber)
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
        room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.Set(roomNumber);
    }
}
