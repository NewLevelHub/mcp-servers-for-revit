using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Services.Views;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Views;

public class ConfigureScheduleTests : RevitApiTest
{
    private static Document _doc;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    private static ViewSchedule CreateDoorSchedule(string name)
    {
        ViewSchedule schedule;
        using (var tx = new Transaction(_doc, "Create test schedule"))
        {
            tx.Start();
            schedule = ViewSchedule.CreateSchedule(_doc, new ElementId(BuiltInCategory.OST_Doors));
            schedule.Name = name;
            // Add a visible schedulable field so we have something to configure.
            var schedulable = schedule.Definition.GetSchedulableFields().FirstOrDefault();
            if (schedulable != null)
                schedule.Definition.AddField(schedulable);
            tx.Commit();
        }
        return schedule;
    }

    // ── configure_schedule: display options ──────────────────────────────────

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Configure_HideTitle_ShowTitleIsFalse()
    {
        var schedule = CreateDoorSchedule("CFG_Test_HideTitle");

        var info = new ConfigureScheduleInfo
        {
            ScheduleId = schedule.Id.IntegerValue,
            ShowTitle = false
        };

        var handler = new ConfigureScheduleEventHandler();
        handler.SetParameters(info);
        handler.Execute(Application.ActiveUIDocument?.Application
                        ?? throw new InvalidOperationException("No UIApplication"));

        await Assert.That(handler.ResultInfo.Success).IsTrue();
        await Assert.That(schedule.Definition.ShowTitle).IsFalse();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Configure_ByName_Succeeds()
    {
        var schedule = CreateDoorSchedule("CFG_Test_ByName");

        var info = new ConfigureScheduleInfo
        {
            ScheduleName = "CFG_Test_ByName",
            ShowGridLines = false
        };

        var handler = new ConfigureScheduleEventHandler();
        handler.SetParameters(info);
        handler.Execute(Application.ActiveUIDocument?.Application
                        ?? throw new InvalidOperationException("No UIApplication"));

        await Assert.That(handler.ResultInfo.Success).IsTrue();
        await Assert.That(schedule.Definition.ShowGridLines).IsFalse();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Configure_UnknownScheduleName_ReturnsFail()
    {
        var info = new ConfigureScheduleInfo { ScheduleName = "NonExistentScheduleXYZ" };

        var handler = new ConfigureScheduleEventHandler();
        handler.SetParameters(info);
        handler.Execute(Application.ActiveUIDocument?.Application
                        ?? throw new InvalidOperationException("No UIApplication"));

        await Assert.That(handler.ResultInfo.Success).IsFalse();
    }

    // ── fit_schedule_to_sheet ─────────────────────────────────────────────────

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task FitSchedule_NarrowColumns_FinalWidthWithinTarget()
    {
        var schedule = CreateDoorSchedule("FIT_Test_Narrow");
        var def = schedule.Definition;

        // Widen the first visible column to something big.
        using (var tx = new Transaction(_doc, "Widen column"))
        {
            tx.Start();
            if (def.GetFieldCount() > 0)
            {
                var field = def.GetField(def.GetFieldId(0));
                field.GridColumnWidth = 500.0 / 304.8; // 500 mm in feet
            }
            tx.Commit();
        }

        var info = new FitScheduleToSheetInfo
        {
            ScheduleId = schedule.Id.IntegerValue,
            MaxWidthMm = 277,
            AllowHideColumns = false,
            AllowNarrowColumns = true,
            AllowLevelFilter = false
        };

        var handler = new FitScheduleToSheetEventHandler();
        handler.SetParameters(info);
        handler.Execute(Application.ActiveUIDocument?.Application
                        ?? throw new InvalidOperationException("No UIApplication"));

        await Assert.That(handler.ResultInfo.Success).IsTrue();
        await Assert.That(handler.ResultInfo.FinalWidthMm)
            .IsLessThanOrEqualTo(277.0 + 0.5); // allow tiny fp rounding
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task FitSchedule_ScheduleAlreadyFits_ReportsFitsTrue()
    {
        var schedule = CreateDoorSchedule("FIT_Test_AlreadyFits");
        var def = schedule.Definition;

        // Set all columns to a narrow width.
        using (var tx = new Transaction(_doc, "Set narrow columns"))
        {
            tx.Start();
            for (var i = 0; i < def.GetFieldCount(); i++)
            {
                var f = def.GetField(def.GetFieldId(i));
                f.GridColumnWidth = 30.0 / 304.8; // 30 mm
            }
            tx.Commit();
        }

        var info = new FitScheduleToSheetInfo
        {
            ScheduleId = schedule.Id.IntegerValue,
            MaxWidthMm = 277
        };

        var handler = new FitScheduleToSheetEventHandler();
        handler.SetParameters(info);
        handler.Execute(Application.ActiveUIDocument?.Application
                        ?? throw new InvalidOperationException("No UIApplication"));

        await Assert.That(handler.ResultInfo.Success).IsTrue();
        await Assert.That(handler.ResultInfo.Fits).IsTrue();
    }
}
