using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Architecture;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Architecture
{
    /// <summary>
    /// Event handler for creating rooms in Revit
    /// </summary>
    public class CreateRoomEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication _uiApp;
        private UIDocument _uiDoc => _uiApp.ActiveUIDocument;
        private Document _doc => _uiDoc.Document;

        /// <summary>
        /// Event wait object for synchronization
        /// </summary>
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        /// <summary>
        /// Room creation data (input)
        /// </summary>
        public List<RoomCreationInfo> RoomData { get; private set; }

        /// <summary>
        /// Execution result (output)
        /// </summary>
        public AIResult<List<RoomResultInfo>> Result { get; private set; }

        /// <summary>
        /// Set the room creation parameters
        /// </summary>
        public void SetParameters(List<RoomCreationInfo> data)
        {
            RoomData = data;
            _resetEvent.Reset();
        }

        /// <summary>
        /// The placed room already covering this plan point on this level, or null.
        ///
        /// Probed a foot above the level so the point sits inside the room volume
        /// rather than exactly on its base, where the test is ambiguous. Revit
        /// answers for the document's last phase; a project whose rooms live in an
        /// earlier phase falls through to the collector below, which is why both
        /// are here.
        /// </summary>
        private Room RoomAtPoint(double xInFeet, double yInFeet, Level level)
        {
            var point = new XYZ(xInFeet, yInFeet, level.Elevation + 1.0);

            try
            {
                if (_doc.GetRoomAtPoint(point) is Room direct && direct.LevelId == level.Id)
                {
                    return direct;
                }
            }
            catch
            {
                // GetRoomAtPoint throws on some phase configurations rather than
                // returning null. Fall through — a missed check only costs the
                // duplicate we were trying to avoid, a thrown exception costs the
                // whole batch.
            }

            foreach (var candidate in new FilteredElementCollector(_doc)
                         .OfCategory(BuiltInCategory.OST_Rooms)
                         .WhereElementIsNotElementType()
                         .Cast<Room>())
            {
                if (candidate.Area <= 0 || candidate.LevelId != level.Id)
                {
                    continue;
                }

                try
                {
                    if (candidate.IsPointInRoom(point))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Unplaced or unenclosed rooms refuse the test; they cannot be
                    // the occupant of anything anyway.
                }
            }

            return null;
        }

        public void Execute(UIApplication uiapp)
        {
            _uiApp = uiapp;

            try
            {
                var createdRooms = new List<RoomResultInfo>();
                // Skipped rooms used to vanish silently, so "created 0 room(s)" came
                // back as a success and the model had no idea what to fix.
                var failures = new List<string>();
                // Points that already had a room. Reported as "already there", never as
                // created — and never as a fresh duplicate on top.
                var skipped = new List<RoomResultInfo>();
                var roomIndex = 0;

                // Get all existing room numbers to avoid duplicates
                HashSet<string> existingRoomNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var existingRooms = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<Room>()
                    .ToList();

                foreach (var existingRoom in existingRooms)
                {
                    if (!string.IsNullOrEmpty(existingRoom.Number))
                    {
                        existingRoomNumbers.Add(existingRoom.Number);
                    }
                }

                // One recorder for the whole batch: what Revit complained about has to
                // reach the answer, or "создано 9 помещений" is all the model ever sees.
                var warnings = new RecordingWarningsPreprocessor();

                foreach (var roomInfo in RoomData)
                {
                    roomIndex++;
                    using (Transaction tx = new Transaction(_doc, "Create Room"))
                    {
                        // Dismiss warnings by severity, not by their English wording.
                        // DuplicateRoomNumberFailurePreprocessor matched "Number" /
                        // "duplicate" in the description — on a Russian Revit those
                        // words never appear, nothing was dismissed, and Revit put the
                        // warning up as a modal dialog inside an ExternalEvent, where
                        // nobody can click it. Every later tool call then waited out its
                        // timeout: «Читаю параметры элемента → ошибка», twice
                        // (20.08.2026).
                        FailureHandlingOptions failureOptions = tx.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(warnings);
                        failureOptions.SetClearAfterRollback(true);
                        failureOptions.SetDelayedMiniWarnings(false);
                        tx.SetFailureHandlingOptions(failureOptions);

                        tx.Start();

                        // Step 1: Find or determine the level
                        Level level = null;
                        if (roomInfo.LevelId > 0)
                        {
                            // Use specified level ID
                            level = _doc.GetElement(new ElementId(roomInfo.LevelId)) as Level;
                        }

                        if (level == null && roomInfo.Location != null)
                        {
                            // Find nearest level to the Z coordinate
                            double zInFeet = roomInfo.Location.Z / 304.8;
                            level = FindNearestLevel(zInFeet);
                        }

                        if (level == null)
                        {
                            // Use the first available level
                            level = new FilteredElementCollector(_doc)
                                .OfClass(typeof(Level))
                                .Cast<Level>()
                                .OrderBy(l => l.Elevation)
                                .FirstOrDefault();
                        }

                        if (level == null)
                        {
                            tx.RollBack();
                            failures.Add($"#{roomIndex}: в проекте нет ни одного уровня для размещения помещения");
                            continue;
                        }

                        // Step 2: Create the room at the specified location
                        Room room = null;

                        if (roomInfo.Location != null)
                        {
                            // Convert mm to feet for UV coordinates (2D point in plan)
                            double xInFeet = roomInfo.Location.X / 304.8;
                            double yInFeet = roomInfo.Location.Y / 304.8;

                            // NewRoom places a room whether or not the region already has
                            // one, and a second room in the same enclosure is what Revit
                            // calls «Избыточная Помещение»: it holds no area, and it is
                            // announced with a modal dialog that freezes the rest of the
                            // turn. Asking first costs nothing and is the only thing that
                            // keeps a repeated request from stacking rooms (20.08.2026).
                            Room occupant = RoomAtPoint(xInFeet, yInFeet, level);
                            if (occupant != null)
                            {
                                tx.RollBack();
                                skipped.Add(new RoomResultInfo
                                {
                                    Id = occupant.Id.GetIntValue(),
                                    UniqueId = occupant.UniqueId,
                                    Name = occupant.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Room",
                                    Number = occupant.Number,
                                    RequestedNumber = roomInfo.Number,
                                    LevelName = level.Name,
                                    Area = occupant.Area,
                                    Perimeter = occupant.Perimeter
                                });
                                continue;
                            }

                            UV locationUV = new UV(xInFeet, yInFeet);

                            // Create room at the specified UV location on the level
                            room = _doc.Create.NewRoom(level, locationUV);
                        }

                        if (room == null)
                        {
                            tx.RollBack();
                            failures.Add(roomInfo.Location == null
                                ? $"#{roomIndex}: не задана точка размещения (location)"
                                : $"#{roomIndex}: точка ({roomInfo.Location.X:F0}, {roomInfo.Location.Y:F0}) мм " +
                                  $"на уровне «{level.Name}» не внутри замкнутого контура стен — " +
                                  "помещение создать не из чего");
                            continue;
                        }

                        // Step 3: Set room properties
                        // Set room name
                        if (!string.IsNullOrEmpty(roomInfo.Name))
                        {
                            Parameter nameParam = room.get_Parameter(BuiltInParameter.ROOM_NAME);
                            if (nameParam != null && !nameParam.IsReadOnly)
                            {
                                nameParam.Set(roomInfo.Name);
                            }
                        }

                        // Set room number (ensuring uniqueness)
                        // IMPORTANT: Generate unique number BEFORE relying on Revit's auto-assigned number
                        // to prevent any duplicate number warnings
                        string roomNumber = roomInfo.Number;
                        if (!string.IsNullOrEmpty(roomNumber))
                        {
                            // User provided a number - make it unique if it already exists
                            roomNumber = GetUniqueRoomNumber(roomNumber, existingRoomNumbers);
                        }
                        else
                        {
                            // No number provided - generate next available number (don't use room.Number)
                            roomNumber = GetNextAvailableRoomNumber(existingRoomNumbers);
                        }

                        Parameter numberParam = room.get_Parameter(BuiltInParameter.ROOM_NUMBER);
                        if (numberParam != null && !numberParam.IsReadOnly)
                        {
                            numberParam.Set(roomNumber);
                            // Add to tracking set to avoid duplicates in same batch
                            existingRoomNumbers.Add(roomNumber);
                        }

                        // Set upper limit if specified
                        if (roomInfo.UpperLimitId > 0)
                        {
                            Parameter upperLimitParam = room.get_Parameter(BuiltInParameter.ROOM_UPPER_LEVEL);
                            if (upperLimitParam != null && !upperLimitParam.IsReadOnly)
                            {
                                upperLimitParam.Set(new ElementId(roomInfo.UpperLimitId));
                            }
                        }

                        // Set limit offset if specified (convert mm to feet)
                        if (roomInfo.LimitOffset > 0)
                        {
                            Parameter limitOffsetParam = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);
                            if (limitOffsetParam != null && !limitOffsetParam.IsReadOnly)
                            {
                                limitOffsetParam.Set(roomInfo.LimitOffset / 304.8);
                            }
                        }

                        // Set base offset if specified (convert mm to feet)
                        if (roomInfo.BaseOffset != 0)
                        {
                            Parameter baseOffsetParam = room.get_Parameter(BuiltInParameter.ROOM_LOWER_OFFSET);
                            if (baseOffsetParam != null && !baseOffsetParam.IsReadOnly)
                            {
                                baseOffsetParam.Set(roomInfo.BaseOffset / 304.8);
                            }
                        }

                        // Set department if provided
                        if (!string.IsNullOrEmpty(roomInfo.Department))
                        {
                            Parameter deptParam = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT);
                            if (deptParam != null && !deptParam.IsReadOnly)
                            {
                                deptParam.Set(roomInfo.Department);
                            }
                        }

                        // Set comments if provided
                        if (!string.IsNullOrEmpty(roomInfo.Comments))
                        {
                            Parameter commentsParam = room.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                            if (commentsParam != null && !commentsParam.IsReadOnly)
                            {
                                commentsParam.Set(roomInfo.Comments);
                            }
                        }

                        tx.Commit();

                        // Add to result list
                        createdRooms.Add(new RoomResultInfo
                        {
                            Id = room.Id.GetIntValue(),
                            UniqueId = room.UniqueId,
                            Name = roomInfo.Name ?? "Room",
                            Number = roomNumber, // Use the actual assigned number (may differ from requested if made unique)
                            RequestedNumber = roomInfo.Number, // Original requested number
                            LevelName = level.Name,
                            Area = room.Area,
                            Perimeter = room.Perimeter
                        });
                    }
                }

                var roomMessage = $"Создано помещений: {createdRooms.Count} из {RoomData.Count}.";
                if (skipped.Count > 0)
                {
                    roomMessage +=
                        $" Пропущено {skipped.Count} — в этих точках помещение уже есть (№"
                        + string.Join(", №", skipped.Select(r => r.Number).Take(10))
                        + "); повторно они не создавались.";
                }
                if (failures.Count > 0)
                    roomMessage += " Не удалось — " + string.Join("; ", failures) + ".";
                foreach (var line in warnings.ToWarningLines("Revit предупредил"))
                    roomMessage += " " + line + ".";

                Result = new AIResult<List<RoomResultInfo>>
                {
                    // Nothing new and nothing skipped is a failure; skipping everything
                    // because it was already there is not — the plan is in the asked-for
                    // state either way, and calling that a failure sends the model round
                    // again.
                    Success = createdRooms.Count > 0 || skipped.Count > 0,
                    Message = roomMessage,
                    Response = createdRooms
                };
            }
            catch (Exception ex)
            {
                // No TaskDialog.Show: this runs inside an ExternalEvent with nobody able
                // to click it during an agent-driven turn — it would hang the chat.
                Result = new AIResult<List<RoomResultInfo>>
                {
                    Success = false,
                    Message = $"Error creating rooms: {ex.Message}",
                };
            }
            finally
            {
                _resetEvent.Set(); // Signal that the operation is complete
            }
        }

        /// <summary>
        /// Find the nearest level to a given elevation
        /// </summary>
        private Level FindNearestLevel(double elevationInFeet)
        {
            var levels = new FilteredElementCollector(_doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToList();

            Level nearestLevel = null;
            double minDistance = double.MaxValue;

            foreach (var level in levels)
            {
                double distance = Math.Abs(level.Elevation - elevationInFeet);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    nearestLevel = level;
                }
            }

            return nearestLevel;
        }

        /// <summary>
        /// Get the next available room number by finding the highest existing number and incrementing
        /// </summary>
        /// <param name="existingNumbers">Set of existing room numbers</param>
        /// <returns>A guaranteed unique room number</returns>
        private string GetNextAvailableRoomNumber(HashSet<string> existingNumbers)
        {
            // Find the highest numeric room number and increment from there
            int maxNumber = 0;
            foreach (string num in existingNumbers)
            {
                // Try to parse the entire string as a number
                if (int.TryParse(num, out int parsed))
                {
                    if (parsed > maxNumber) maxNumber = parsed;
                }
                else
                {
                    // Try to extract trailing digits (e.g., "Room 101" -> 101)
                    string digits = "";
                    for (int i = num.Length - 1; i >= 0; i--)
                    {
                        if (char.IsDigit(num[i]))
                            digits = num[i] + digits;
                        else if (digits.Length > 0)
                            break;
                    }
                    if (digits.Length > 0 && int.TryParse(digits, out int trailingNum))
                    {
                        if (trailingNum > maxNumber) maxNumber = trailingNum;
                    }
                }
            }

            // Start from maxNumber + 1 and find next available
            for (int i = maxNumber + 1; i < maxNumber + 10000; i++)
            {
                string candidate = i.ToString();
                if (!existingNumbers.Contains(candidate))
                {
                    return candidate;
                }
            }

            // Fallback (should never reach here)
            return (maxNumber + 1).ToString();
        }

        /// <summary>
        /// Get a unique room number by adding a suffix if the number already exists
        /// </summary>
        /// <param name="baseNumber">The desired room number</param>
        /// <param name="existingNumbers">Set of existing room numbers</param>
        /// <returns>A unique room number</returns>
        private string GetUniqueRoomNumber(string baseNumber, HashSet<string> existingNumbers)
        {
            if (string.IsNullOrEmpty(baseNumber))
            {
                baseNumber = "1";
            }

            // If the number doesn't exist, use it as-is
            if (!existingNumbers.Contains(baseNumber))
            {
                return baseNumber;
            }

            // Try to extract numeric portion and increment
            // Handle cases like "101", "101A", "Room 101", etc.
            string prefix = "";
            string numericPart = "";
            string suffix = "";

            // Find the last sequence of digits in the string
            int lastDigitEnd = -1;
            int lastDigitStart = -1;
            for (int i = baseNumber.Length - 1; i >= 0; i--)
            {
                if (char.IsDigit(baseNumber[i]))
                {
                    if (lastDigitEnd == -1) lastDigitEnd = i;
                    lastDigitStart = i;
                }
                else if (lastDigitEnd != -1)
                {
                    break;
                }
            }

            if (lastDigitStart != -1)
            {
                prefix = baseNumber.Substring(0, lastDigitStart);
                numericPart = baseNumber.Substring(lastDigitStart, lastDigitEnd - lastDigitStart + 1);
                suffix = baseNumber.Substring(lastDigitEnd + 1);

                // Try incrementing the numeric part
                if (int.TryParse(numericPart, out int num))
                {
                    int maxAttempts = 1000;
                    for (int i = 1; i <= maxAttempts; i++)
                    {
                        string candidate = prefix + (num + i).ToString().PadLeft(numericPart.Length, '0') + suffix;
                        if (!existingNumbers.Contains(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }

            // Fallback: append a letter suffix (A, B, C, ...)
            for (char c = 'A'; c <= 'Z'; c++)
            {
                string candidate = baseNumber + c;
                if (!existingNumbers.Contains(candidate))
                {
                    return candidate;
                }
            }

            // Last resort: append a number
            for (int i = 2; i <= 1000; i++)
            {
                string candidate = baseNumber + "-" + i;
                if (!existingNumbers.Contains(candidate))
                {
                    return candidate;
                }
            }

            // Should never reach here, but just in case
            return baseNumber + "-" + Guid.NewGuid().ToString().Substring(0, 4);
        }

        /// <summary>
        /// Wait for the operation to complete
        /// </summary>
        /// <param name="timeoutMilliseconds">Timeout in milliseconds</param>
        /// <returns>True if completed before timeout</returns>
        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            // Do not Reset here - SetParameters/Prepare already Reset before Raise.
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        /// <summary>
        /// IExternalEventHandler.GetName implementation
        /// </summary>
        public string GetName()
        {
            return "Create Room";
        }
    }

    /// <summary>
    /// Result information for a created room
    /// </summary>
    public class RoomResultInfo
    {
        public int Id { get; set; }
        public string UniqueId { get; set; }
        public string Name { get; set; }
        public string Number { get; set; }
        public string RequestedNumber { get; set; } // Original requested number (may differ from Number if made unique)
        public string LevelName { get; set; }
        public double Area { get; set; }
        public double Perimeter { get; set; }
    }
}
