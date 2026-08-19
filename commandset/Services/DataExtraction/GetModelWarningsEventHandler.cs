using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    /// <summary>
    /// Reads Revit's own warning list — the «Просмотр предупреждений» dialog,
    /// which nothing in the tool set could reach before (REV-47).
    /// </summary>
    /// <remarks>
    /// Read-only: no transaction, no model change. Warnings are held by the
    /// document, so this is a cheap call even on a large model — the cost is in
    /// resolving categories, which is why element samples are capped.
    /// </remarks>
    public class GetModelWarningsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private int _maxElementIdsPerGroup = 20;
        private string _severityFilter = string.Empty;

        public GetModelWarningsResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        public void SetParameters(int maxElementIdsPerGroup = 20, string severity = "")
        {
            _maxElementIdsPerGroup = Math.Max(0, maxElementIdsPerGroup);
            _severityFilter = severity ?? string.Empty;
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;
                var warnings = doc.GetWarnings();

                var buckets = new Dictionary<string, WarningBucket>(StringComparer.Ordinal);
                int total = 0;
                int errors = 0;

                foreach (var warning in warnings)
                {
                    var severity = warning.GetSeverity().ToString();
                    if (!string.IsNullOrWhiteSpace(_severityFilter) &&
                        !severity.Equals(_severityFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    total++;
                    if (!severity.Equals("Warning", StringComparison.OrdinalIgnoreCase))
                    {
                        errors++;
                    }

                    var description = warning.GetDescriptionText() ?? string.Empty;
                    if (!buckets.TryGetValue(description, out var bucket))
                    {
                        bucket = new WarningBucket { Description = description, Severity = severity };
                        buckets[description] = bucket;
                    }

                    bucket.Count++;

                    // Both lists matter: Revit puts the second party of a clash in
                    // "additional", and an overlap report naming one wall is useless.
                    foreach (var id in warning.GetFailingElements())
                    {
                        bucket.ElementIds.Add(id);
                    }
                    foreach (var id in warning.GetAdditionalElements())
                    {
                        bucket.ElementIds.Add(id);
                    }
                }

                var groups = buckets.Values
                    .Select(bucket => BuildGroup(doc, bucket, _maxElementIdsPerGroup))
                    .OrderByDescending(group => group.Count)
                    .ThenBy(group => group.Description, StringComparer.CurrentCulture)
                    .ToList();

                ResultInfo = new GetModelWarningsResult
                {
                    Success = true,
                    Message = total == 0
                        ? "В модели нет предупреждений."
                        : $"Предупреждений: {total} в {groups.Count} видах.",
                    TotalWarnings = total,
                    TotalGroups = groups.Count,
                    ErrorCount = errors,
                    Groups = groups
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new GetModelWarningsResult
                {
                    Success = false,
                    Message = $"Не удалось прочитать предупреждения модели: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private static ModelWarningGroup BuildGroup(Document doc, WarningBucket bucket, int maxIds)
        {
            var ids = bucket.ElementIds.ToList();

            var categories = ids
                .Select(id => doc.GetElement(id)?.Category?.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .GroupBy(name => name!, StringComparer.CurrentCulture)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .Take(6)
                .ToList();

            var sample = ids.Take(maxIds).Select(id => id.GetValue()).ToList();

            return new ModelWarningGroup
            {
                Description = bucket.Description,
                Severity = bucket.Severity,
                Count = bucket.Count,
                ElementCount = ids.Count,
                Categories = categories,
                ElementIds = sample,
                ElementIdsTruncated = sample.Count < ids.Count ? true : null
            };
        }

        /// <summary>Occurrences of one warning text, before category resolution.</summary>
        private sealed class WarningBucket
        {
            public string Description { get; set; } = string.Empty;
            public string Severity { get; set; } = string.Empty;
            public int Count { get; set; }
            /// A set: the same element is named by many occurrences of the same warning.
            public HashSet<ElementId> ElementIds { get; } = new();
        }

        public string GetName() => "Get Model Warnings";
    }
}
