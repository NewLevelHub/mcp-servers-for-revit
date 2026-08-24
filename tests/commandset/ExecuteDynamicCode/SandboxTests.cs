using System.Diagnostics;
using Autodesk.Revit.DB;
// RevitAPIUI also carries an internal UIApplication in the global namespace, which an
// unqualified name binds to first, so the public one has to be brought in explicitly.
using Autodesk.Revit.UI;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Commands.ExecuteDynamicCode;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.ExecuteDynamicCode;

/// <summary>
/// REV-175: sandbox for send_code_to_revit — trial rollback, the touched-element limit, the
/// loop timeout/iteration guard, and the filesystem/network denylist. Each test drives
/// <see cref="ExecuteCodeEventHandler" /> directly, the same way the other Commands tests drive
/// their EventHandler classes.
/// </summary>
public class SandboxTests : RevitApiTest
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

    private static UIApplication RequireUiApp()
    {
        return Nice3point.Revit.Toolkit.Context.UiApplication
               ?? throw new InvalidOperationException("No UIApplication");
    }

    private static int LevelCountInRange(double minFeet, double maxFeet)
    {
        return new FilteredElementCollector(_doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .Count(l => l.Elevation >= minFeet && l.Elevation < maxFeet);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Trial_CreatesLevel_LeavesNoTraceAndReportsIntent()
    {
        var before = new FilteredElementCollector(_doc).WhereElementIsNotElementType().GetElementCount();

        var handler = new ExecuteCodeEventHandler();
        handler.SetExecutionParameters(
            code: "var level = Level.Create(document, 500.0); level.Name = \"REV-175 trial\"; return \"ok\";",
            parameters: null,
            transactionMode: ExecuteCodeEventHandler.TransactionModeTrial);
        handler.Execute(RequireUiApp());

        // Category display names are localized (RU/EN flip with Revit's UI language — see
        // [[revit-ui-language-switches]]), so read the expected name from the model instead of
        // hardcoding it.
        var levelsCategoryName = Category.GetCategory(_doc, BuiltInCategory.OST_Levels)?.Name ?? "Levels";

        await Assert.That(handler.ResultInfo.Success).IsTrue();
        await Assert.That(handler.ResultInfo.IsTrial).IsTrue();
        await Assert.That(handler.ResultInfo.IntentReport).Contains("создаст 1");
        await Assert.That(handler.ResultInfo.IntentReport).Contains(levelsCategoryName);
        await Assert.That(handler.ResultInfo.TotalChangedElements).IsEqualTo(1);

        var after = new FilteredElementCollector(_doc).WhereElementIsNotElementType().GetElementCount();
        await Assert.That(after).IsEqualTo(before);
        await Assert.That(LevelCountInRange(499.5, 500.5)).IsEqualTo(0);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Delete_PastConfiguredLimit_RollsBackAndReportsLimit()
    {
        // Five real levels — a small stand-in for the ticket's "tries to delete 1000", kept
        // small so the test stays fast. The limit is set explicitly below rather than relying
        // on the (much higher) default, so the scenario is deterministic regardless of default.
        using (var setup = new Transaction(_doc, "REV-175 setup: 5 levels"))
        {
            setup.Start();
            for (var i = 0; i < 5; i++)
                Level.Create(_doc, 600.0 + i);
            setup.Commit();
        }

        await Assert.That(LevelCountInRange(599.5, 605.5)).IsEqualTo(5);

        var handler = new ExecuteCodeEventHandler();
        handler.SetExecutionParameters(
            code: @"
                var levels = new FilteredElementCollector(document)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .Where(l => l.Elevation >= 599.5 && l.Elevation < 605.5)
                    .ToList();
                foreach (var lvl in levels)
                    document.Delete(lvl.Id);
                return levels.Count;",
            parameters: null,
            transactionMode: ExecuteCodeEventHandler.TransactionModeAuto,
            maxChangedElements: 2);
        handler.Execute(RequireUiApp());

        await Assert.That(handler.ResultInfo.Success).IsFalse();
        await Assert.That(handler.ResultInfo.ErrorMessage).Contains("лимит");
        await Assert.That(handler.ResultInfo.TotalChangedElements).IsEqualTo(5);

        // Nothing actually applied — the transaction was rolled back.
        await Assert.That(LevelCountInRange(599.5, 605.5)).IsEqualTo(5);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task InfiniteLoop_StoppedByTimeout_RevitStaysUsable()
    {
        var stopwatch = Stopwatch.StartNew();

        var handler = new ExecuteCodeEventHandler();
        handler.SetExecutionParameters(
            code: "while (true) { }",
            parameters: null,
            transactionMode: ExecuteCodeEventHandler.TransactionModeTrial,
            maxChangedElements: 0,
            timeoutSeconds: 1);
        handler.Execute(RequireUiApp());
        stopwatch.Stop();

        await Assert.That(handler.ResultInfo.Success).IsFalse();
        await Assert.That(handler.ResultInfo.ErrorMessage).Contains("таймаут");
        // Bounded well below the 60s+ an unguarded infinite loop would otherwise cost — proves
        // the loop was actually interrupted, not that it happened to finish on its own.
        await Assert.That(stopwatch.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));

        // Revit's UI thread is free again — a normal call right after has to still work.
        var followUp = new ExecuteCodeEventHandler();
        followUp.SetExecutionParameters("return 1 + 1;");
        followUp.Execute(RequireUiApp());

        await Assert.That(followUp.ResultInfo.Success).IsTrue();
        await Assert.That(followUp.ResultInfo.Result).IsEqualTo("2");
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task TightLoop_StoppedByIterationBudget_NotJustTimeout()
    {
        var handler = new ExecuteCodeEventHandler();
        handler.SetExecutionParameters(
            code: "for (long i = 0; i < 50000000; i++) { } return 0;",
            parameters: null,
            transactionMode: ExecuteCodeEventHandler.TransactionModeTrial,
            maxChangedElements: 0,
            timeoutSeconds: 30); // generous, so the iteration budget is what actually fires

        handler.Execute(RequireUiApp());

        await Assert.That(handler.ResultInfo.Success).IsFalse();
        await Assert.That(handler.ResultInfo.ErrorMessage).Contains("итера");
        await Assert.That(handler.ResultInfo.ErrorMessage)
            .Contains(ExecuteCodeEventHandler.LoopIterationBudget.ToString());
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task FileSystemApi_Blocked()
    {
        var handler = new ExecuteCodeEventHandler();
        handler.SetExecutionParameters(
            code: "return System.IO.File.Exists(\"C:\\\\\");",
            parameters: null,
            transactionMode: ExecuteCodeEventHandler.TransactionModeTrial);
        handler.Execute(RequireUiApp());

        await Assert.That(handler.ResultInfo.Success).IsFalse();
        await Assert.That(handler.ResultInfo.ErrorMessage).Contains("запрещённый API");
        await Assert.That(handler.ResultInfo.ErrorMessage).Contains("File");
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task NetworkApi_Blocked()
    {
        var handler = new ExecuteCodeEventHandler();
        handler.SetExecutionParameters(
            code: "return System.Net.Dns.GetHostName();",
            parameters: null,
            transactionMode: ExecuteCodeEventHandler.TransactionModeTrial);
        handler.Execute(RequireUiApp());

        await Assert.That(handler.ResultInfo.Success).IsFalse();
        await Assert.That(handler.ResultInfo.ErrorMessage).Contains("запрещённый API");
        await Assert.That(handler.ResultInfo.ErrorMessage).Contains("Dns");
    }
}
