using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Architecture;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Architecture
{
    /// <summary>
    /// REV-153: returns a door/window FamilySymbol of the requested size, duplicating the
    /// source type and setting its width/height when the project has nothing close enough.
    /// <para>
    /// Tracing a DWG produces real dimensions — 789 mm windows, 917 mm doors — and a project
    /// template rarely stocks those. Placing the nearest stock size instead is a silent
    /// dimensional error in the model, so do what a person does: duplicate the type and type
    /// the size in.
    /// </para>
    /// </summary>
    public class EnsureOpeningTypeEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new(false);

        private UIApplication uiApp;
        private Document doc => uiApp.ActiveUIDocument.Document;

        public OpeningTypeRequestInfo RequestInfo { get; private set; }
        public OpeningTypeResultInfo ResultInfo { get; private set; } = new();

        public void SetParameters(OpeningTypeRequestInfo request)
        {
            RequestInfo = request;
            ResultInfo = new OpeningTypeResultInfo();
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 15000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName() => "Подбор типоразмера проёма";

        public void Execute(UIApplication app)
        {
            uiApp = app;

            try
            {
                var request = RequestInfo;
                if (request == null || request.WidthMm <= 0)
                {
                    Fail("widthMm is required and must be positive.");
                    return;
                }

                if (!(doc.GetElement(new ElementId(request.SourceTypeId)) is FamilySymbol source))
                {
                    Fail($"sourceTypeId {request.SourceTypeId} is not a FamilySymbol. " +
                         "Call get_available_family_types first.");
                    return;
                }

                double targetWidthFt = request.WidthMm / 304.8;
                double targetHeightFt = request.HeightMm > 0 ? request.HeightMm / 304.8 : 0;
                double tolFt = Math.Max(request.ToleranceMm, 0.5) / 304.8;

                // An existing type of the right size beats making another one — projects fill up
                // with near-duplicate types fast otherwise.
                var existing = FindMatchingSymbol(source, targetWidthFt, targetHeightFt, tolFt);
                if (existing != null)
                {
                    Succeed(existing, created: false, "Existing type matched the traced size.");
                    return;
                }

                using (var transaction = new Transaction(doc, "Create opening type by size"))
                {
                    var failOpts = transaction.GetFailureHandlingOptions();
                    failOpts.SetFailuresPreprocessor(new RecordingWarningsPreprocessor());
                    transaction.SetFailureHandlingOptions(failOpts);
                    transaction.Start();

                    string name = string.IsNullOrWhiteSpace(request.TypeName)
                        ? BuildTypeName(request, source)
                        : request.TypeName.Trim();
                    name = UniqueTypeName(source, name);

                    if (!(source.Duplicate(name) is FamilySymbol created))
                    {
                        transaction.RollBack();
                        Fail($"Duplicate('{name}') did not return a FamilySymbol.");
                        return;
                    }

                    if (!TrySetLength(created, targetWidthFt, WidthParameters, out var widthErr))
                    {
                        transaction.RollBack();
                        Fail($"Could not set width on the new type: {widthErr}");
                        return;
                    }

                    if (targetHeightFt > 0)
                    {
                        // A family with a locked height is still usable at the right width.
                        if (!TrySetLength(created, targetHeightFt, HeightParameters, out var heightErr))
                            ResultInfo.Message = $"Height left as the source type's: {heightErr}";
                    }

                    if (!created.IsActive)
                        created.Activate();

                    doc.Regenerate();
                    transaction.Commit();

                    Succeed(created, created: true, ResultInfo.Message);
                }
            }
            catch (Exception ex)
            {
                Fail($"Error resolving opening type: {ex.Message}");
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private static readonly BuiltInParameter[] WidthParameters =
        {
            BuiltInParameter.DOOR_WIDTH,
            BuiltInParameter.WINDOW_WIDTH,
            BuiltInParameter.FAMILY_WIDTH_PARAM,
            BuiltInParameter.GENERIC_WIDTH
        };

        private static readonly BuiltInParameter[] HeightParameters =
        {
            BuiltInParameter.DOOR_HEIGHT,
            BuiltInParameter.WINDOW_HEIGHT,
            BuiltInParameter.FAMILY_HEIGHT_PARAM,
            BuiltInParameter.GENERIC_HEIGHT
        };

        /// <summary>Any writable width/height parameter will do — families name them differently.</summary>
        private static bool TrySetLength(
            FamilySymbol symbol,
            double valueFt,
            BuiltInParameter[] candidates,
            out string error)
        {
            foreach (var bip in candidates)
            {
                var param = symbol.get_Parameter(bip);
                if (param == null || param.IsReadOnly || param.StorageType != StorageType.Double)
                    continue;
                if (param.Set(valueFt))
                {
                    error = null;
                    return true;
                }
            }

            error = "no writable width/height parameter on this family";
            return false;
        }

        private FamilySymbol FindMatchingSymbol(
            FamilySymbol source,
            double widthFt,
            double heightFt,
            double tolFt)
        {
            var family = source.Family;
            if (family == null)
                return null;

            foreach (var id in family.GetFamilySymbolIds())
            {
                if (!(doc.GetElement(id) is FamilySymbol symbol))
                    continue;

                double w = ReadLength(symbol, WidthParameters);
                if (w <= 0 || Math.Abs(w - widthFt) > tolFt)
                    continue;

                if (heightFt > 0)
                {
                    double h = ReadLength(symbol, HeightParameters);
                    if (h > 0 && Math.Abs(h - heightFt) > tolFt)
                        continue;
                }

                return symbol;
            }

            return null;
        }

        private static double ReadLength(FamilySymbol symbol, BuiltInParameter[] candidates)
        {
            foreach (var bip in candidates)
            {
                var param = symbol.get_Parameter(bip);
                if (param != null && param.StorageType == StorageType.Double)
                    return param.AsDouble();
            }

            return 0;
        }

        private static string BuildTypeName(OpeningTypeRequestInfo request, FamilySymbol source)
        {
            double heightMm = request.HeightMm > 0
                ? request.HeightMm
                : ReadLength(source, HeightParameters) * 304.8;

            return heightMm > 0
                ? $"{Math.Round(request.WidthMm)} x {Math.Round(heightMm)} мм"
                : $"{Math.Round(request.WidthMm)} мм";
        }

        /// <summary>Duplicate() throws on a name clash, so step the name until it is free.</summary>
        private string UniqueTypeName(FamilySymbol source, string desired)
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var family = source.Family;
            if (family != null)
                foreach (var id in family.GetFamilySymbolIds())
                    if (doc.GetElement(id) is FamilySymbol s)
                        taken.Add(s.Name);

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

        private void Succeed(FamilySymbol symbol, bool created, string message)
        {
            ResultInfo = new OpeningTypeResultInfo
            {
                Success = true,
                TypeId = symbol.Id.GetIntValue(),
                TypeName = symbol.Name,
                FamilyName = symbol.FamilyName ?? string.Empty,
                WidthMm = Math.Round(ReadLength(symbol, WidthParameters) * 304.8, 1),
                HeightMm = Math.Round(ReadLength(symbol, HeightParameters) * 304.8, 1),
                Created = created,
                Message = message ?? string.Empty
            };
        }

        private void Fail(string message)
        {
            ResultInfo = new OpeningTypeResultInfo { Success = false, Message = message };
        }
    }
}
