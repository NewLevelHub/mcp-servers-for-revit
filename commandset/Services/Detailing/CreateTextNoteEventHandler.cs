using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.Detailing;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Detailing;

public class CreateTextNoteEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private const double MmPerFoot = 304.8;

    private TextNoteCreationInfo _info;
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public TextNoteCreationResult ResultInfo { get; private set; } = new TextNoteCreationResult();
    public bool TaskCompleted { get; private set; }

    public void SetParameters(TextNoteCreationInfo info)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        TaskCompleted = false;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 60000)
    {
        // Do not Reset here — SetParameters already Reset; resetting after a fast
        // Execute can clear the signal and hang until timeout.
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = app.ActiveUIDocument.Document;
            ResultInfo = Create(doc, _info);
        }
        catch (Exception ex)
        {
            ResultInfo = new TextNoteCreationResult
            {
                Success = false,
                Message = $"Error creating text note: {ex.Message}"
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public string GetName() => "Create Text Note";

    public static TextNoteCreationResult Create(Document doc, TextNoteCreationInfo info)
    {
        if (doc == null)
            throw new ArgumentNullException(nameof(doc));
        if (info == null)
            throw new ArgumentNullException(nameof(info));
        if (string.IsNullOrWhiteSpace(info.Text))
            throw new ArgumentException("Text is required.");
        if (info.Position == null)
            throw new ArgumentException("Position is required (mm).");

        var warnings = new List<string>();

        var view = ResolveView(doc, info)
            ?? throw new ArgumentException("Target view was not found. Provide viewId, viewUniqueId, or viewName.");

        var textTypeId = ResolveTextNoteType(doc, info.TextTypeName, warnings);

        TextNote note;
        var hasLeader = false;

        using (var tx = new Transaction(doc, "Create Text Note"))
        {
            tx.Start();

            var position = JZPoint.ToXYZ(info.Position);
            var options = new TextNoteOptions { TypeId = textTypeId };

            if (info.Width > 0)
            {
                var width = ClampTextWidth(doc, textTypeId, MmToFeet(info.Width));
                note = TextNote.Create(doc, view.Id, position, width, info.Text.Trim(), options);
            }
            else
            {
                note = TextNote.Create(doc, view.Id, position, info.Text.Trim(), options);
            }

            if (info.LeaderEnd != null)
            {
                try
                {
                    var leaderEnd = JZPoint.ToXYZ(info.LeaderEnd);
                    var leader = note.AddLeader(leaderEnd.X <= position.X
                        ? TextNoteLeaderTypes.TNLT_STRAIGHT_L
                        : TextNoteLeaderTypes.TNLT_STRAIGHT_R);
                    leader.End = leaderEnd;
                    hasLeader = true;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Failed to add leader: {ex.Message}");
                }
            }

            tx.Commit();
        }

        return new TextNoteCreationResult
        {
            Success = true,
            Message = $"Successfully created text note on view '{view.Name}'.",
            TextNoteId = note.Id.GetValue(),
            TextNoteUniqueId = note.UniqueId,
            ViewId = view.Id.GetValue(),
            TextType = (doc.GetElement(textTypeId) as TextNoteType)?.Name ?? string.Empty,
            HasLeader = hasLeader,
            Warnings = warnings
        };
    }

    private static double ClampTextWidth(Document doc, ElementId textTypeId, double requestedWidth)
    {
        var minWidth = TextNote.GetMinimumAllowedWidth(doc, textTypeId);
        var maxWidth = TextNote.GetMaximumAllowedWidth(doc, textTypeId);
        return Math.Min(Math.Max(requestedWidth, minWidth), maxWidth);
    }

    private static ElementId ResolveTextNoteType(Document doc, string typeName, List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var byName = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault(type =>
                    type.Name.Equals(typeName.Trim(), StringComparison.OrdinalIgnoreCase));

            if (byName != null)
                return byName.Id;

            warnings.Add($"Text note type '{typeName}' was not found; the default text type is used.");
        }

        var defaultTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        if (defaultTypeId != ElementId.InvalidElementId && doc.GetElement(defaultTypeId) is TextNoteType)
            return defaultTypeId;

        var firstType = new FilteredElementCollector(doc)
            .OfClass(typeof(TextNoteType))
            .Cast<TextNoteType>()
            .FirstOrDefault();

        if (firstType == null)
            throw new InvalidOperationException("The project has no text note types.");

        return firstType.Id;
    }

    private static View ResolveView(Document doc, TextNoteCreationInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.ViewUniqueId))
        {
            if (doc.GetElement(info.ViewUniqueId.Trim()) is View byUniqueId && !byUniqueId.IsTemplate)
                return byUniqueId;
        }

        if (info.ViewId > 0)
        {
            if (doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(info.ViewId)) is View byId && !byId.IsTemplate)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(info.ViewName))
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => !view.IsTemplate && !(view is ViewSheet))
                .FirstOrDefault(view =>
                    view.Name.Equals(info.ViewName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static double MmToFeet(double millimeters) => millimeters / MmPerFoot;
}
