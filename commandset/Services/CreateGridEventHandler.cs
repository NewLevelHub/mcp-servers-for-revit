using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Architecture;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class CreateGridEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication uiApp;
        private UIDocument uiDoc => uiApp.ActiveUIDocument;
        private Document doc => uiDoc.Document;

        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public GridCreationInfo Parameters { get; private set; }

        public AIResult<List<GridCreationResult>> Result { get; private set; }

        public void SetParameters(GridCreationInfo parameters)
        {
            Parameters = parameters;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            uiApp = uiapp;

            try
            {
                if (!Parameters.Validate(out string validationError))
                {
                    Result = new AIResult<List<GridCreationResult>>
                    {
                        Success = false,
                        Message = $"Validation failed: {validationError}",
                        Response = null
                    };
                    return;
                }

                List<GridCreationResult> createdGrids = new List<GridCreationResult>();
                List<Grid> createdGridElements = new List<Grid>();
                List<string> displayWarnings = new List<string>();

                var existingGridNames = new FilteredElementCollector(doc)
                    .OfClass(typeof(Grid))
                    .Cast<Grid>()
                    .Select(g => g.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                ResolvePositionsAndExtents(out var xPositions, out var yPositions, displayWarnings);

                if (xPositions.Count == 0 && yPositions.Count == 0)
                {
                    Result = new AIResult<List<GridCreationResult>>
                    {
                        Success = false,
                        Message = "No grid positions to create. " + string.Join(" ", displayWarnings),
                        Response = null
                    };
                    return;
                }

                if (Parameters.XExtentMin >= Parameters.XExtentMax ||
                    Parameters.YExtentMin >= Parameters.YExtentMax)
                {
                    Result = new AIResult<List<GridCreationResult>>
                    {
                        Success = false,
                        Message = "Invalid grid extents after resolution. Provide extents or use autoFromWalls.",
                        Response = null
                    };
                    return;
                }

                using (Transaction trans = new Transaction(doc, "Create Grid System"))
                {
                    trans.Start();

                    var xLabels = GenerateLabels(
                        xPositions.Count,
                        Parameters.XStartLabel,
                        Parameters.XNamingStyle);

                    for (int i = 0; i < xPositions.Count; i++)
                    {
                        double xPos = xPositions[i];
                        string label = xLabels[i];
                        string uniqueLabel = GetUniqueGridName(label, existingGridNames);
                        existingGridNames.Add(uniqueLabel);

                        XYZ startPoint = new XYZ(
                            xPos / 304.8,
                            Parameters.YExtentMin / 304.8,
                            Parameters.Elevation / 304.8);

                        XYZ endPoint = new XYZ(
                            xPos / 304.8,
                            Parameters.YExtentMax / 304.8,
                            Parameters.Elevation / 304.8);

                        Line gridLine = Line.CreateBound(startPoint, endPoint);
                        Grid grid = Grid.Create(doc, gridLine);
                        grid.Name = uniqueLabel;
                        GridDisplayHelper.EnsureGridSpansAllLevels(doc, grid);
                        createdGridElements.Add(grid);

#if REVIT2024_OR_GREATER
                        long gridId = grid.Id.Value;
#else
                        long gridId = grid.Id.IntegerValue;
#endif

                        createdGrids.Add(new GridCreationResult
                        {
                            ElementId = gridId,
                            Name = uniqueLabel,
                            OriginalName = label,
                            WasRenamed = label != uniqueLabel,
                            Axis = "X",
                            Position = xPos
                        });
                    }

                    var yLabels = GenerateLabels(
                        yPositions.Count,
                        Parameters.YStartLabel,
                        Parameters.YNamingStyle);

                    for (int i = 0; i < yPositions.Count; i++)
                    {
                        double yPos = yPositions[i];
                        string label = yLabels[i];
                        string uniqueLabel = GetUniqueGridName(label, existingGridNames);
                        existingGridNames.Add(uniqueLabel);

                        XYZ startPoint = new XYZ(
                            Parameters.XExtentMin / 304.8,
                            yPos / 304.8,
                            Parameters.Elevation / 304.8);

                        XYZ endPoint = new XYZ(
                            Parameters.XExtentMax / 304.8,
                            yPos / 304.8,
                            Parameters.Elevation / 304.8);

                        Line gridLine = Line.CreateBound(startPoint, endPoint);
                        Grid grid = Grid.Create(doc, gridLine);
                        grid.Name = uniqueLabel;
                        GridDisplayHelper.EnsureGridSpansAllLevels(doc, grid);
                        createdGridElements.Add(grid);

#if REVIT2024_OR_GREATER
                        long gridId = grid.Id.Value;
#else
                        long gridId = grid.Id.IntegerValue;
#endif

                        createdGrids.Add(new GridCreationResult
                        {
                            ElementId = gridId,
                            Name = uniqueLabel,
                            OriginalName = label,
                            WasRenamed = label != uniqueLabel,
                            Axis = "Y",
                            Position = yPos
                        });
                    }

                    // Always apply bubble/extent config (at least on active floor plan).
                    // ConfigureDisplayOnAllPlans controls whether all plans or only active.
                    if (createdGridElements.Count > 0)
                    {
                        var displayResult = GridDisplayHelper.ConfigureGrids(
                            doc,
                            createdGridElements,
                            GridDisplayHelper.FromCreationInfo(Parameters),
                            doc.ActiveView as ViewPlan);
                        displayWarnings.AddRange(displayResult.Warnings);
                    }

                    trans.Commit();
                }

                int renamedCount = createdGrids.Count(g => g.WasRenamed);
                string mode = Parameters.AutoFromWalls
                    ? "from structural wall centerlines"
                    : (Parameters.HasExplicitXPositions || Parameters.HasExplicitYPositions)
                        ? "from explicit positions"
                        : "from spacing";

                string message =
                    $"Successfully created {createdGrids.Count} grids " +
                    $"({xPositions.Count} X-axis + {yPositions.Count} Y-axis) {mode}";

                if (Parameters.ConfigureDisplayOnAllPlans)
                    message += ". Grid display configured on all floor plans.";
                else
                    message += ". Grid display configured on the active floor plan.";

                if (renamedCount > 0)
                    message += $". {renamedCount} grid(s) were renamed to avoid duplicates.";

                if (displayWarnings.Count > 0)
                    message += $" {displayWarnings.Count} warning(s): " + string.Join("; ", displayWarnings.Take(5));

                Result = new AIResult<List<GridCreationResult>>
                {
                    Success = true,
                    Message = message,
                    Response = createdGrids
                };
            }
            catch (Exception ex)
            {
                // No TaskDialog.Show: this runs inside an ExternalEvent with nobody able
                // to click it during an agent-driven turn — it would hang the chat.
                Result = new AIResult<List<GridCreationResult>>
                {
                    Success = false,
                    Message = $"Failed to create grids: {ex.Message}",
                    Response = null
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private void ResolvePositionsAndExtents(
            out List<double> xPositions,
            out List<double> yPositions,
            List<string> warnings)
        {
            xPositions = new List<double>();
            yPositions = new List<double>();

            if (Parameters.AutoFromWalls)
            {
                var levelId = ResolveLevelId(Parameters.LevelName);
                var plan = GridAlignmentHelper.ComputeFromWalls(
                    doc,
                    levelId,
                    Parameters.WallFilter,
                    Parameters.MinWallThicknessMm,
                    Parameters.ClusterToleranceMm,
                    Parameters.ExtentOvershootMm);

                warnings.AddRange(plan.Warnings);
                xPositions = plan.XPositionsMm;
                yPositions = plan.YPositionsMm;

                if (Parameters.ShouldAutoComputeExtents() &&
                    plan.XExtentMaxMm > plan.XExtentMinMm &&
                    plan.YExtentMaxMm > plan.YExtentMinMm)
                {
                    Parameters.XExtentMin = plan.XExtentMinMm;
                    Parameters.XExtentMax = plan.XExtentMaxMm;
                    Parameters.YExtentMin = plan.YExtentMinMm;
                    Parameters.YExtentMax = plan.YExtentMaxMm;
                }

                return;
            }

            xPositions = Parameters.HasExplicitXPositions
                ? Parameters.XPositionsMm.Distinct().OrderBy(v => v).ToList()
                : GeneratePositions(Parameters.XCount, Parameters.XSpacing, Parameters.XStartPosition);

            yPositions = Parameters.HasExplicitYPositions
                ? Parameters.YPositionsMm.Distinct().OrderBy(v => v).ToList()
                : GeneratePositions(Parameters.YCount, Parameters.YSpacing, Parameters.YStartPosition);

            if (Parameters.ShouldAutoComputeExtents() &&
                (xPositions.Count > 0 || yPositions.Count > 0))
            {
                ApplyExtentsFromPositions(xPositions, yPositions);
            }
        }

        private void ApplyExtentsFromPositions(List<double> xPositions, List<double> yPositions)
        {
            double overshoot = Parameters.ExtentOvershootMm >= 0
                ? Parameters.ExtentOvershootMm
                : GridAlignmentHelper.DefaultExtentOvershootMm;

            if (xPositions.Count > 0)
            {
                // Horizontal grids need X extents covering vertical grid span
                Parameters.XExtentMin = xPositions.Min() - overshoot;
                Parameters.XExtentMax = xPositions.Max() + overshoot;
            }

            if (yPositions.Count > 0)
            {
                Parameters.YExtentMin = yPositions.Min() - overshoot;
                Parameters.YExtentMax = yPositions.Max() + overshoot;
            }

            // If only one axis has positions, keep other extent from params or expand symmetrically
            if (xPositions.Count == 0 && yPositions.Count > 0)
            {
                // leave X extents as provided
            }

            if (yPositions.Count == 0 && xPositions.Count > 0)
            {
                // leave Y extents as provided
            }
        }

        private ElementId ResolveLevelId(string levelName)
        {
            if (!string.IsNullOrWhiteSpace(levelName))
            {
                var level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(l =>
                        string.Equals(l.Name, levelName.Trim(), StringComparison.OrdinalIgnoreCase));

                if (level == null)
                    throw new InvalidOperationException($"Level '{levelName}' was not found.");

                return level.Id;
            }

            if (uiDoc.ActiveView is ViewPlan plan && plan.GenLevel != null)
                return plan.GenLevel.Id;

            var anyLevel = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault();

            if (anyLevel == null)
                throw new InvalidOperationException("No levels found in the document.");

            return anyLevel.Id;
        }

        private List<double> GeneratePositions(int count, double spacing, double startPosition)
        {
            List<double> positions = new List<double>();
            for (int i = 0; i < count; i++)
                positions.Add(startPosition + (i * spacing));
            return positions;
        }

        private List<string> GenerateLabels(int count, string startLabel, string namingStyle)
        {
            List<string> labels = new List<string>();
            if (count <= 0)
                return labels;

            // Auto-detect Cyrillic start label
            if (namingStyle == "alphabetic" && IsCyrillicLabel(startLabel))
                namingStyle = "cyrillic";

            if (namingStyle == "numeric")
            {
                if (!int.TryParse(startLabel, out int startNum))
                    startNum = 1;

                for (int i = 0; i < count; i++)
                    labels.Add((startNum + i).ToString());
            }
            else if (namingStyle == "cyrillic")
            {
                int startIndex = IndexOfCyrillic(startLabel);
                for (int i = 0; i < count; i++)
                    labels.Add(CyrillicLetterAt(startIndex + i));
            }
            else
            {
                string upperStart = startLabel.ToUpperInvariant();
                char startChar = upperStart.Length > 0 ? upperStart[0] : 'A';
                if (!char.IsLetter(startChar) || startChar < 'A' || startChar > 'Z')
                    startChar = 'A';

                for (int i = 0; i < count; i++)
                    labels.Add(GenerateAlphabeticLabel(startChar, i));
            }

            return labels;
        }

        /// <summary>
        /// Russian architectural grid letters (skips Ё, Й, Ъ, Ы, Ь — common office practice).
        /// </summary>
        private static readonly char[] CyrillicGridLetters =
        {
            'А', 'Б', 'В', 'Г', 'Д', 'Е', 'Ж', 'И', 'К', 'Л', 'М',
            'Н', 'П', 'Р', 'С', 'Т', 'У', 'Ф', 'Х', 'Ц', 'Ч', 'Ш', 'Э', 'Ю', 'Я'
        };

        private static bool IsCyrillicLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return false;
            char c = char.ToUpper(label.Trim()[0]);
            return c >= 'А' && c <= 'Я';
        }

        private static int IndexOfCyrillic(string startLabel)
        {
            if (string.IsNullOrWhiteSpace(startLabel))
                return 0;

            char c = char.ToUpper(startLabel.Trim()[0]);
            int idx = Array.IndexOf(CyrillicGridLetters, c);
            return idx >= 0 ? idx : 0;
        }

        private static string CyrillicLetterAt(int index)
        {
            if (index < 0)
                index = 0;

            if (index < CyrillicGridLetters.Length)
                return CyrillicGridLetters[index].ToString();

            // Beyond alphabet: АА, АБ, ...
            int overflow = index - CyrillicGridLetters.Length;
            int first = overflow / CyrillicGridLetters.Length;
            int second = overflow % CyrillicGridLetters.Length;
            return $"{CyrillicGridLetters[first]}{CyrillicGridLetters[second]}";
        }

        private string GenerateAlphabeticLabel(char startChar, int offset)
        {
            int charIndex = (startChar - 'A') + offset;

            if (charIndex < 26)
                return ((char)('A' + charIndex)).ToString();

            string result = "";
            int remaining = charIndex;
            while (remaining >= 0)
            {
                int mod = remaining % 26;
                result = ((char)('A' + mod)) + result;
                remaining = (remaining / 26) - 1;
                if (remaining < 0) break;
            }

            return result;
        }

        private string GetUniqueGridName(string baseName, HashSet<string> existingNames)
        {
            string candidateName = baseName;
            int counter = 1;
            while (existingNames.Contains(candidateName))
            {
                candidateName = $"{baseName}{counter}";
                counter++;
            }

            return candidateName;
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            // Do not Reset here - SetParameters/Prepare already Reset before Raise.
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName()
        {
            return "Create Grid System";
        }
    }

    public class GridCreationResult
    {
        [Newtonsoft.Json.JsonProperty("elementId")]
        public long ElementId { get; set; }

        [Newtonsoft.Json.JsonProperty("name")]
        public string Name { get; set; }

        [Newtonsoft.Json.JsonProperty("originalName")]
        public string OriginalName { get; set; }

        [Newtonsoft.Json.JsonProperty("wasRenamed")]
        public bool WasRenamed { get; set; }

        [Newtonsoft.Json.JsonProperty("axis")]
        public string Axis { get; set; }

        [Newtonsoft.Json.JsonProperty("position")]
        public double Position { get; set; }
    }
}
