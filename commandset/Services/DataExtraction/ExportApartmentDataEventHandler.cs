using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    public class ExportApartmentDataEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        /// <summary>
        /// Room parameter candidates for the apartment number, checked in order
        /// (org shared params first, then common RU names).
        /// </summary>
        private static readonly string[] ApartmentParameterCandidates =
        {
            "ADSK_Номер квартиры",
            "АДСК_Номер квартиры",
            "Номер квартиры",
            "КВ_Номер квартиры",
            "Квартира",
            "Apartment Number"
        };

        private string _apartmentParameter;
        private bool _includeRooms;

        public ExportApartmentDataResult ResultInfo { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetParameters(string apartmentParameter = null, bool includeRooms = false)
        {
            _apartmentParameter = apartmentParameter?.Trim() ?? string.Empty;
            _includeRooms = includeRooms;
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var doc = app.ActiveUIDocument.Document;
                ResultInfo = Compute(doc, _apartmentParameter, _includeRooms);
            }
            catch (Exception ex)
            {
                ResultInfo = new ExportApartmentDataResult
                {
                    Success = false,
                    Message = $"Error exporting apartment data: {ex.Message}"
                };
            }
            finally
            {
                if (ResultInfo != null)
                {
                    stopwatch.Stop();
                    ResultInfo.ExecutionTimeMs = stopwatch.ElapsedMilliseconds;
                }

                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public static ExportApartmentDataResult Compute(Document doc, string apartmentParameter, bool includeRooms)
        {
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Room>()
                .Where(room => room.Area > 0)
                .ToList();

            if (rooms.Count == 0)
            {
                return new ExportApartmentDataResult
                {
                    Success = false,
                    Message = "The model has no placed rooms with area — nothing to group into apartments."
                };
            }

            string parameterName = ResolveApartmentParameter(rooms, apartmentParameter, out var discoveryHint);
            if (parameterName == null)
            {
                return new ExportApartmentDataResult
                {
                    Success = false,
                    Message =
                        "Apartment number parameter was not found on rooms. " +
                        $"Tried: {string.Join(", ", ApartmentParameterCandidates)}. " +
                        (discoveryHint.Length > 0
                            ? $"Room parameters that look apartment-related: {discoveryHint}. "
                            : string.Empty) +
                        "Pass apartmentNumberParameter explicitly."
                };
            }

            var inputs = rooms
                .Select(room => new ApartmentRoomInput
                {
                    Id = room.Id.GetValue(),
                    Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? room.Name ?? string.Empty,
                    Level = room.Level?.Name ?? string.Empty,
                    AreaM2 = RevitUnitConversion.ToSquareMeters(room.Area),
                    ApartmentNumber = GetParameterValue(room, parameterName)
                })
                .ToList();

            return ApartmentAggregator.Aggregate(doc.Title, parameterName, inputs, includeRooms);
        }

        /// <summary>
        /// Explicit name wins; otherwise the first candidate with at least one non-empty
        /// room value. Returns null with a hint of apartment-looking parameter names.
        /// </summary>
        private static string ResolveApartmentParameter(
            IReadOnlyList<Room> rooms,
            string requested,
            out string discoveryHint)
        {
            discoveryHint = string.Empty;

            if (!string.IsNullOrWhiteSpace(requested))
                return requested.Trim();

            foreach (var candidate in ApartmentParameterCandidates)
            {
                if (rooms.Any(room => GetParameterValue(room, candidate).Length > 0))
                    return candidate;
            }

            var apartmentLike = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Parameter parameter in rooms[0].Parameters)
            {
                var name = parameter.Definition?.Name;
                if (name != null &&
                    (name.IndexOf("кварт", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     name.IndexOf("apart", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    apartmentLike.Add(name);
                }
            }

            discoveryHint = string.Join(", ", apartmentLike);
            return null;
        }

        private static string GetParameterValue(Room room, string parameterName)
        {
            var parameter = room.LookupParameter(parameterName);
            if (parameter == null || !parameter.HasValue)
                return string.Empty;

            var value = parameter.StorageType == StorageType.String
                ? parameter.AsString()
                : parameter.AsValueString();

            return value?.Trim() ?? string.Empty;
        }

        public string GetName() => "Export Apartment Data";
    }
}
