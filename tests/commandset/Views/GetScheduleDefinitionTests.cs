using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Services.Views;
using RevitMCPCommandSet.Utils;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Views;

public class GetScheduleDefinitionTests : RevitApiTest
{
    private static Document _doc;
    private static ViewSchedule _schedule;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Get Schedule Definition Test");
        tx.Start();

        _schedule = ViewSchedule.CreateSchedule(_doc, new ElementId(BuiltInCategory.OST_Doors));
        _schedule.Name = "MCP Get Schedule Definition Test";

        var definition = _schedule.Definition;
        definition.ShowTitle = true;
        definition.ShowHeaders = false;
        definition.ShowGridLines = true;

        SchedulableField markField = null;
        SchedulableField typeMarkField = null;
        foreach (var schedulableField in definition.GetSchedulableFields())
        {
            var name = schedulableField.GetName(_doc);
            if (name.Equals("Mark", StringComparison.OrdinalIgnoreCase))
                markField = schedulableField;
            else if (name.Equals("Type Mark", StringComparison.OrdinalIgnoreCase))
                typeMarkField = schedulableField;
        }

        ScheduleField markScheduleField = null;
        if (markField != null)
        {
            markScheduleField = definition.AddField(markField);
            markScheduleField.ColumnHeading = "Door Mark";
            markScheduleField.GridColumnWidth = 25.0 / 304.8;
            markScheduleField.HorizontalAlignment = ScheduleHorizontalAlignment.Center;
        }

        if (typeMarkField != null)
            definition.AddField(typeMarkField);

        if (markScheduleField != null)
        {
            definition.AddFilter(new ScheduleFilter(
                markScheduleField.FieldId,
                ScheduleFilterType.NotEqual,
                string.Empty));
            definition.AddSortGroupField(new ScheduleSortGroupField(
                markScheduleField.FieldId,
                ScheduleSortOrder.Descending));
        }

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task GetScheduleDefinition_ByName_ReturnsFieldsFiltersSortsAndFlags()
    {
        var result = GetScheduleDefinitionEventHandler.Compute(
            _doc,
            scheduleName: "MCP Get Schedule Definition Test");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.ScheduleName).IsEqualTo("MCP Get Schedule Definition Test");
        await Assert.That(result.ShowTitle).IsTrue();
        await Assert.That(result.ShowHeaders).IsFalse();
        await Assert.That(result.ShowGridLines).IsTrue();
        await Assert.That(result.Fields.Count).IsGreaterThan(0);

        var markField = result.Fields.FirstOrDefault(field =>
            field.ParameterName.Equals("Mark", StringComparison.OrdinalIgnoreCase));
        await Assert.That(markField).IsNotNull();
        await Assert.That(markField!.Heading).IsEqualTo("Door Mark");
        await Assert.That(markField.Width).IsEqualTo(25.0).Within(0.5);
        await Assert.That(markField.HorizontalAlignment).IsEqualTo("Center");
        await Assert.That(markField.FieldIndex).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task GetScheduleDefinition_ById_ReturnsSameSchedule()
    {
#if REVIT2024_OR_GREATER
        var scheduleId = _schedule.Id.Value;
#else
        var scheduleId = (long)_schedule.Id.GetIntValue();
#endif

        var result = GetScheduleDefinitionEventHandler.Compute(_doc, scheduleId: scheduleId);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.ScheduleUniqueId).IsEqualTo(_schedule.UniqueId);
        await Assert.That(result.CategoryId).IsEqualTo((int)BuiltInCategory.OST_Doors);
    }

    [Test]
    public async Task GetScheduleDefinition_ByUniqueId_ReturnsFiltersAndSorts()
    {
        var result = GetScheduleDefinitionEventHandler.Compute(
            _doc,
            scheduleUniqueId: _schedule.UniqueId);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Filters.Count).IsGreaterThan(0);
        await Assert.That(result.Filters[0].FilterType).IsEqualTo("NotEqual");
        await Assert.That(result.SortFields.Count).IsGreaterThan(0);
        await Assert.That(result.SortFields[0].SortOrder).IsEqualTo("Descending");
        await Assert.That(result.GroupFields).IsEmpty();
    }

    [Test]
    public async Task GetScheduleDefinition_MissingSchedule_ReturnsFailure()
    {
        var result = GetScheduleDefinitionEventHandler.Compute(
            _doc,
            scheduleName: "Schedule That Does Not Exist");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Message).Contains("was not found");
    }
}
