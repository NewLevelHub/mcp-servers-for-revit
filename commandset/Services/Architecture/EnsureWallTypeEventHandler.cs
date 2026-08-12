using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Architecture;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Architecture
{
    /// <summary>
    /// REV-154: returns a WallType of the requested thickness, duplicating the source type and
    /// widening its structural layer when the project has nothing close enough.
    /// <para>
    /// Tracing a DWG measures the real gap between wall faces — 193 mm, 147 mm, 406 mm — and a
    /// template rarely stocks those. Snapping to the nearest stock type silently redraws the
    /// building at the wrong thickness, so do what a person does: duplicate and type the size in.
    /// </para>
    /// </summary>
    public class EnsureWallTypeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new(false);

        private UIApplication uiApp;
        private Document doc => uiApp.ActiveUIDocument.Document;

        public WallTypeRequestInfo RequestInfo { get; private set; }
        public WallTypeResultInfo ResultInfo { get; private set; } = new();

        public void SetParameters(WallTypeRequestInfo request)
        {
            RequestInfo = request;
            ResultInfo = new WallTypeResultInfo();
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 15000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName() => "Подбор типа стены по толщине";

        public void Execute(UIApplication app)
        {
            uiApp = app;

            try
            {
                var request = RequestInfo;
                if (request == null || request.ThicknessMm <= 0)
                {
                    Fail("thicknessMm is required and must be positive.");
                    return;
                }

                if (!(doc.GetElement(new ElementId(request.SourceTypeId)) is WallType source))
                {
                    Fail($"sourceTypeId {request.SourceTypeId} is not a WallType. " +
                         "Call get_available_family_types with categoryList=[OST_Walls] first.");
                    return;
                }

                double targetFt = request.ThicknessMm / 304.8;
                double tolFt = Math.Max(request.ToleranceMm, 0.5) / 304.8;

                // An existing type of the right thickness beats making another one — projects
                // fill up with near-duplicate types fast otherwise.
                var existing = FindMatchingType(source, targetFt, tolFt);
                if (existing != null)
                {
                    Succeed(existing, created: false, "Existing wall type matched the traced thickness.");
                    return;
                }

                var structure = source.GetCompoundStructure();
                if (structure == null)
                {
                    Fail($"Wall type '{source.Name}' has no compound structure — its thickness " +
                         "cannot be set (curtain walls are sized by their panels, not layers).");
                    return;
                }

                using (var transaction = new Transaction(doc, "Create wall type by thickness"))
                {
                    var failOpts = transaction.GetFailureHandlingOptions();
                    failOpts.SetFailuresPreprocessor(new RecordingWarningsPreprocessor());
                    transaction.SetFailureHandlingOptions(failOpts);
                    transaction.Start();

                    string name = string.IsNullOrWhiteSpace(request.TypeName)
                        ? $"{source.Name} {Math.Round(request.ThicknessMm)}мм"
                        : request.TypeName.Trim();
                    name = UniqueTypeName(name);

                    if (!(source.Duplicate(name) is WallType created))
                    {
                        transaction.RollBack();
                        Fail($"Duplicate('{name}') did not return a WallType.");
                        return;
                    }

                    if (!TrySetThickness(created, targetFt, out var error))
                    {
                        transaction.RollBack();
                        Fail($"Could not set thickness on the new type: {error}");
                        return;
                    }

                    doc.Regenerate();
                    transaction.Commit();

                    Succeed(created, created: true, string.Empty);
                }
            }
            catch (Exception ex)
            {
                Fail($"Error resolving wall type: {ex.Message}");
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        /// <summary>
        /// Puts the whole difference into one layer: the structural one when the type declares it,
        /// otherwise the thickest. Spreading it across layers would change the build-up, which is
        /// not ours to decide.
        /// </summary>
        private static bool TrySetThickness(WallType wallType, double targetFt, out string error)
        {
            var structure = wallType.GetCompoundStructure();
            if (structure == null)
            {
                error = "no compound structure";
                return false;
            }

            var layers = structure.GetLayers();
            if (layers.Count == 0)
            {
                error = "compound structure has no layers";
                return false;
            }

            int index = structure.StructuralMaterialIndex;
            if (index < 0 || index >= layers.Count || layers[index].Width <= 0)
            {
                index = -1;
                double widest = 0;
                for (var i = 0; i < layers.Count; i++)
                {
                    // A membrane layer is zero-width by definition and cannot absorb the delta.
                    if (layers[i].Function == MaterialFunctionAssignment.Membrane) continue;
                    if (layers[i].Width > widest)
                    {
                        widest = layers[i].Width;
                        index = i;
                    }
                }
            }

            if (index < 0)
            {
                error = "no layer that can carry a thickness";
                return false;
            }

            double delta = targetFt - structure.GetWidth();
            double newWidth = layers[index].Width + delta;
            if (newWidth <= 0)
            {
                error =
                    $"target {Math.Round(targetFt * 304.8)} mm is thinner than the type's other " +
                    "layers — duplicate a thinner type instead";
                return false;
            }

            try
            {
                structure.SetLayerWidth(index, newWidth);
                wallType.SetCompoundStructure(structure);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            error = null;
            return true;
        }

        private WallType FindMatchingType(WallType source, double targetFt, double tolFt)
        {
            WallType best = null;
            double bestDelta = double.MaxValue;

            foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(WallType)))
            {
                if (!(element is WallType candidate)) continue;
                if (candidate.Kind != source.Kind) continue;

                double width = candidate.Width;
                if (width <= 0) continue;

                double delta = Math.Abs(width - targetFt);
                if (delta > tolFt || delta >= bestDelta) continue;

                bestDelta = delta;
                best = candidate;
            }

            return best;
        }

        /// <summary>Duplicate() throws on a name clash, so step the name until it is free.</summary>
        private string UniqueTypeName(string desired)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(WallType)))
                if (element is WallType wallType)
                    taken.Add(wallType.Name);

            if (!taken.Contains(desired))
                return desired;

            for (var i = 2; i < 100; i++)
            {
                var candidate = $"{desired} ({i})";
                if (!taken.Contains(candidate))
                    return candidate;
            }

            return $"{desired} {Guid.NewGuid():N}".Substring(0, 60);
        }

        private void Succeed(WallType wallType, bool created, string message)
        {
            ResultInfo = new WallTypeResultInfo
            {
                Success = true,
                TypeId = wallType.Id.GetIntValue(),
                TypeName = wallType.Name,
                ThicknessMm = Math.Round(wallType.Width * 304.8, 1),
                Created = created,
                Message = message ?? string.Empty
            };
        }

        private void Fail(string message)
        {
            ResultInfo = new WallTypeResultInfo { Success = false, Message = message };
        }
    }
}
