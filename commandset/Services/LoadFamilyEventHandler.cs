using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;
using IOPath = System.IO.Path;

namespace RevitMCPCommandSet.Services;

/// <summary>
///     Loads .rfa families into the project. Without this, detail component families a node needs
///     have to be loaded by hand before place_detail_component has anything to place.
/// </summary>
public class LoadFamilyEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private LoadFamilyRequestInfo _info;
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public LoadFamilyResult ResultInfo { get; private set; } = new LoadFamilyResult();
    public bool TaskCompleted { get; private set; }

    public void SetParameters(LoadFamilyRequestInfo info)
    {
        _info = info ?? new LoadFamilyRequestInfo();
        TaskCompleted = false;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 120000)
    {
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            ResultInfo = Load(app.ActiveUIDocument.Document, _info);
        }
        catch (Exception ex)
        {
            ResultInfo = new LoadFamilyResult
            {
                Success = false,
                Message = $"Error loading families: {ex.Message}"
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public string GetName() => "Load Family";

    public static LoadFamilyResult Load(Document doc, LoadFamilyRequestInfo info)
    {
        if (doc == null)
            throw new ArgumentNullException(nameof(doc));

        info ??= new LoadFamilyRequestInfo();

        var result = new LoadFamilyResult();
        var paths = ResolvePaths(info, result.Warnings);

        result.RequestedCount = paths.Count;
        if (paths.Count == 0)
        {
            throw new ArgumentException(
                "No family files to load. Pass paths to .rfa files, or a directory " +
                "(optionally with names). Paths are read on the machine running Revit.");
        }

        var options = new OverwriteFamilyLoadOptions(info.OverwriteParameterValues);

        using (var tx = new Transaction(doc, "MCP Load Families"))
        {
            tx.Start();

            foreach (var path in paths)
            {
                var entry = new LoadedFamilyInfo { Path = path };
                result.Families.Add(entry);

                try
                {
                    if (!doc.LoadFamily(path, options, out var family) || family == null)
                    {
                        // LoadFamily returns false when the family is already in the project and
                        // OnFamilyFound declined, so look it up before calling it a failure.
                        family = FindLoadedFamily(doc, IOPath.GetFileNameWithoutExtension(path));
                        if (family == null)
                        {
                            entry.Error = "Revit refused to load this family.";
                            continue;
                        }

                        result.Warnings.Add(
                            $"'{family.Name}' was already in the project; the existing family is reported.");
                    }

                    entry.Loaded = true;
                    entry.FamilyId = family.Id.GetValue();
                    entry.FamilyName = family.Name;
                    entry.Category = family.FamilyCategory?.Name ?? string.Empty;
                    entry.Types = CollectTypes(doc, family, info.ActivateSymbols);
                }
                catch (Exception ex)
                {
                    entry.Error = ex.Message;
                }
            }

            tx.Commit();
        }

        result.LoadedCount = result.Families.Count(family => family.Loaded);
        result.Success = result.LoadedCount > 0;
        result.Message = result.Success
            ? $"Loaded {result.LoadedCount} of {result.RequestedCount} families."
            : "No families were loaded.";

        return result;
    }

    private static List<LoadedFamilyTypeInfo> CollectTypes(Document doc, Family family, bool activate)
    {
        var types = new List<LoadedFamilyTypeInfo>();

        foreach (var symbolId in family.GetFamilySymbolIds())
        {
            if (doc.GetElement(symbolId) is not FamilySymbol symbol)
                continue;

            if (activate && !symbol.IsActive)
                symbol.Activate();

            types.Add(new LoadedFamilyTypeInfo
            {
                TypeId = symbol.Id.GetValue(),
                TypeName = symbol.Name
            });
        }

        return types.OrderBy(type => type.TypeName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Family FindLoadedFamily(Document doc, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .FirstOrDefault(family => family.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Turns the request into a list of existing .rfa files. A missing or non-.rfa path is a
    ///     named warning here rather than a raw Revit exception later.
    /// </summary>
    private static List<string> ResolvePaths(LoadFamilyRequestInfo info, List<string> warnings)
    {
        var candidates = new List<string>();

        foreach (var path in info.Paths ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(path))
                candidates.Add(path.Trim());
        }

        var directory = info.Directory?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(directory))
        {
            if (!System.IO.Directory.Exists(directory))
            {
                warnings.Add($"Directory '{directory}' does not exist.");
            }
            else if (info.Names != null && info.Names.Count > 0)
            {
                foreach (var name in info.Names.Where(name => !string.IsNullOrWhiteSpace(name)))
                {
                    var fileName = name.Trim();
                    if (!fileName.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                        fileName += ".rfa";

                    candidates.Add(IOPath.Combine(directory, fileName));
                }
            }
            else
            {
                candidates.AddRange(System.IO.Directory.GetFiles(directory, "*.rfa"));
            }
        }

        var resolved = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (!candidate.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"'{candidate}' is not a .rfa file and was skipped.");
                continue;
            }

            if (!System.IO.File.Exists(candidate))
            {
                warnings.Add($"'{candidate}' was not found on the machine running Revit.");
                continue;
            }

            if (seen.Add(IOPath.GetFullPath(candidate)))
                resolved.Add(candidate);
        }

        return resolved;
    }

    private class OverwriteFamilyLoadOptions : IFamilyLoadOptions
    {
        private readonly bool _overwriteParameterValues;

        public OverwriteFamilyLoadOptions(bool overwriteParameterValues)
        {
            _overwriteParameterValues = overwriteParameterValues;
        }

        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = _overwriteParameterValues;
            return true;
        }

        public bool OnSharedFamilyFound(
            Family sharedFamily,
            bool familyInUse,
            out FamilySource source,
            out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = _overwriteParameterValues;
            return true;
        }
    }
}
