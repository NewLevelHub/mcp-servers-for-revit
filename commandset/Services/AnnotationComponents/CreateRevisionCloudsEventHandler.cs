using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Annotation;
using RevitMCPCommandSet.Utils;
using RevitMCPCommandSet.Utils.Detailing;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.AnnotationComponents;

/// <summary>
///     Turns a compare_model_versions diff — already clustered by
///     utils/revisionClouds.ts — into actual Revit annotation: a Revision (found
///     or created), one RevisionCloud per cluster on the right level's plan, and
///     — because Revit does this natively once a cloud sits on a view that is on
///     a sheet — an entry in that sheet's revision table (REV-172).
///     <para>
///     Nothing here decides which changes belong in which cloud; that already
///     happened in TypeScript and is tested there without Revit. This class only
///     answers what TypeScript cannot see: which view is level X's plan that is
///     actually on a sheet, and whether a cloud for this exact signature already
///     exists.
///     </para>
/// </summary>
public class CreateRevisionCloudsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    /// <summary>Comments prefix a cloud this tool drew carries — how a re-run recognises its own work.</summary>
    public const string SignaturePrefix = "MCP-DIFF:";

    private RevisionCloudsCreationInfo _info;
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public RevisionCloudsCreationResult ResultInfo { get; private set; } = new RevisionCloudsCreationResult();
    public bool TaskCompleted { get; private set; }

    public void SetParameters(RevisionCloudsCreationInfo info)
    {
        _info = info ?? new RevisionCloudsCreationInfo();
        TaskCompleted = false;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 60000)
    {
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            ResultInfo = Create(app.ActiveUIDocument.Document, _info);
        }
        catch (Exception ex)
        {
            ResultInfo = new RevisionCloudsCreationResult
            {
                Success = false,
                Message = $"Error creating revision clouds: {ex.Message}"
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public string GetName() => "Create Revision Clouds";

    public static RevisionCloudsCreationResult Create(Document doc, RevisionCloudsCreationInfo info)
    {
        if (doc == null)
            throw new ArgumentNullException(nameof(doc));

        info ??= new RevisionCloudsCreationInfo();

        if (string.IsNullOrWhiteSpace(info.RevisionDescription))
            throw new ArgumentException("revisionDescription is required.");

        if (info.Clusters == null || info.Clusters.Count == 0)
            throw new ArgumentException("At least one cluster is required.");

        var result = new RevisionCloudsCreationResult();
        var warnings = new List<string>();

        using (var tx = new Transaction(doc, "MCP Revision Clouds"))
        {
            tx.Start();

            var revision = FindOrCreateRevision(doc, info.RevisionDescription);
            result.RevisionId = revision.Id.GetValue();
            result.RevisionNumber = revision.SequenceNumber;

            // Built once, not per cluster: a scan per cluster would be O(clusters × clouds already
            // in the document), and a model with a few выдачи behind it can have hundreds of clouds.
            var existingSignatures = CollectExistingSignatures(doc);
            var viewportsByViewId = new FilteredElementCollector(doc)
                .OfClass(typeof(Viewport))
                .Cast<Viewport>()
                .GroupBy(vp => vp.ViewId)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var cluster in info.Clusters)
            {
                if (existingSignatures.Contains(cluster.Signature))
                {
                    result.Skipped.Add(new SkippedRevisionCloudItem
                    {
                        Level = cluster.Level,
                        Signature = cluster.Signature,
                        ChangeCount = cluster.ChangeCount,
                        Reason = "already exists"
                    });
                    continue;
                }

                var view = ResolveViewForLevel(doc, cluster.Level, info.ViewMap, viewportsByViewId, warnings);
                if (view == null)
                {
                    result.Skipped.Add(new SkippedRevisionCloudItem
                    {
                        Level = cluster.Level,
                        Signature = cluster.Signature,
                        ChangeCount = cluster.ChangeCount,
                        Reason = "no view found for level"
                    });
                    continue;
                }

                try
                {
                    var cloud = DrawCloud(doc, view, revision.Id, cluster);

                    viewportsByViewId.TryGetValue(view.Id, out var viewport);
                    var sheet = viewport != null ? doc.GetElement(viewport.SheetId) as ViewSheet : null;

                    result.Created.Add(new CreatedRevisionCloudItem
                    {
                        CloudId = cloud.Id.GetValue(),
                        Level = cluster.Level,
                        ViewName = view.Name,
                        SheetNumber = sheet?.SheetNumber ?? string.Empty,
                        SheetName = sheet?.Name ?? string.Empty,
                        Signature = cluster.Signature,
                        ChangeCount = cluster.ChangeCount
                    });

                    if (sheet == null)
                    {
                        warnings.Add(
                            $"«{view.Name}» (уровень «{cluster.Level}») не размещён ни на одном листе — " +
                            "облако создано, но в таблицу ревизий пока не попадёт.");
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"Уровень «{cluster.Level}»: {ex.Message}");
                    result.Skipped.Add(new SkippedRevisionCloudItem
                    {
                        Level = cluster.Level,
                        Signature = cluster.Signature,
                        ChangeCount = cluster.ChangeCount,
                        Reason = ex.Message
                    });
                }
            }

            tx.Commit();
        }

        result.Warnings = warnings;

        var alreadyExisted = result.Skipped.Count(s => s.Reason == "already exists");
        // False only when every single cluster failed to place — a re-run that finds nothing new
        // to draw (everything already there) is the tool working as intended, not a failure.
        result.Success = result.Created.Count > 0 || alreadyExisted > 0;
        result.Message = result.Created.Count > 0
            ? $"Ревизия №{result.RevisionNumber}: {result.Created.Count} облак{(result.Created.Count == 1 ? "о" : "")} создано, {result.Skipped.Count} пропущено."
            : alreadyExisted > 0
                ? $"Ревизия №{result.RevisionNumber}: новых облаков нет — все {alreadyExisted} уже были нарисованы прошлым запуском."
                : $"Ничего не создано: все {result.Skipped.Count} кластеров пропущены (см. warnings).";

        return result;
    }

    /// <summary>
    ///     Reuses an unissued revision with the same description rather than creating a second one —
    ///     the same "не плодит дубли" rule the ticket asks of clouds applies one level up. Once a
    ///     revision is issued Revit refuses new clouds against it anyway, so an issued match is not
    ///     a candidate.
    /// </summary>
    private static Revision FindOrCreateRevision(Document doc, string description)
    {
        var wanted = description.Trim();
        var existing = new FilteredElementCollector(doc)
            .OfClass(typeof(Revision))
            .Cast<Revision>()
            .FirstOrDefault(r => !r.Issued && string.Equals(r.Description, wanted, StringComparison.Ordinal));

        if (existing != null)
            return existing;

        var revision = Revision.Create(doc);
        revision.Description = wanted;
        revision.RevisionDate = DateTime.Now.ToString("dd.MM.yyyy");
        return revision;
    }

    /// <summary>Every signature already tagged on a cloud in the document, from any previous run.</summary>
    private static HashSet<string> CollectExistingSignatures(Document doc)
    {
        var signatures = new HashSet<string>(StringComparer.Ordinal);

        var clouds = new FilteredElementCollector(doc)
            .OfClass(typeof(RevisionCloud))
            .Cast<RevisionCloud>();

        foreach (var cloud in clouds)
        {
            var comments = cloud.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
            if (string.IsNullOrEmpty(comments) || !comments.StartsWith(SignaturePrefix, StringComparison.Ordinal))
                continue;

            var rest = comments.Substring(SignaturePrefix.Length);
            var spaceIndex = rest.IndexOf(' ');
            signatures.Add(spaceIndex >= 0 ? rest.Substring(0, spaceIndex) : rest);
        }

        return signatures;
    }

    private static RevisionCloud DrawCloud(Document doc, View view, ElementId revisionId, RevisionCloudClusterInfo cluster)
    {
        var z = DetailDrawing.ViewPlaneZ(view);
        var points = new List<XYZ>
        {
            DetailDrawing.ToViewPoint(cluster.MinXMm, cluster.MinYMm, z),
            DetailDrawing.ToViewPoint(cluster.MaxXMm, cluster.MinYMm, z),
            DetailDrawing.ToViewPoint(cluster.MaxXMm, cluster.MaxYMm, z),
            DetailDrawing.ToViewPoint(cluster.MinXMm, cluster.MaxYMm, z)
        };
        var curves = DetailDrawing.BuildClosedCurves(points);

        var cloud = RevisionCloud.Create(doc, view, revisionId, curves);

        var comments = cloud.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (comments != null && !comments.IsReadOnly)
        {
            var text = string.IsNullOrWhiteSpace(cluster.Comment)
                ? $"{SignaturePrefix}{cluster.Signature}"
                : $"{SignaturePrefix}{cluster.Signature} {cluster.Comment}";
            comments.Set(text);
        }

        return cloud;
    }

    /// <summary>
    ///     A level's plan view: an explicit override from <paramref name="viewMap" /> when given and
    ///     found; otherwise the one non-template <see cref="ViewPlan" /> of that level that is placed
    ///     on a sheet. Several such views, or none placed on a sheet at all, are reported rather than
    ///     guessed silently.
    /// </summary>
    private static View ResolveViewForLevel(
        Document doc,
        string level,
        List<RevisionCloudViewOverride> viewMap,
        Dictionary<ElementId, Viewport> viewportsByViewId,
        List<string> warnings)
    {
        var overrideName = viewMap?
            .FirstOrDefault(v => string.Equals(v.Level, level, StringComparison.OrdinalIgnoreCase))?
            .ViewName;

        if (!string.IsNullOrWhiteSpace(overrideName))
        {
            var byName = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && !(v is ViewSheet) &&
                                      v.Name.Equals(overrideName.Trim(), StringComparison.OrdinalIgnoreCase));

            if (byName != null)
                return byName;

            warnings.Add($"Уровень «{level}»: вид «{overrideName}» из viewMap не найден — включён авто-подбор.");
        }

        var candidates = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(v => !v.IsTemplate && v.GenLevel != null &&
                        string.Equals(v.GenLevel.Name, level, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            warnings.Add($"Уровень «{level}»: план этого уровня в проекте не найден.");
            return null;
        }

        var onSheet = candidates.Where(v => viewportsByViewId.ContainsKey(v.Id)).ToList();

        if (onSheet.Count == 1)
            return onSheet[0];

        if (onSheet.Count > 1)
        {
            warnings.Add(
                $"Уровень «{level}»: на листах несколько планов ({string.Join(", ", onSheet.Select(v => v.Name))}) " +
                $"— взят «{onSheet[0].Name}». Уточните через viewMap.");
            return onSheet[0];
        }

        warnings.Add(
            $"Уровень «{level}»: план «{candidates[0].Name}» не размещён ни на одном листе — облако будет " +
            "создано, но в таблицу ревизий не попадёт, пока вид не окажется на листе.");
        return candidates[0];
    }
}
