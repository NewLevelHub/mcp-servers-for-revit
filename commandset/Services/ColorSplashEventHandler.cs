using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    /// <summary>
    /// Colors elements in the active view.
    /// For Rooms (Помещения): applies Revit Color Fill Scheme (цветовая схема) —
    /// View Properties → Color Scheme. This is NOT Annotate → Filled Region
    /// («Цветовая область») — use create_filled_regions for that.
    /// For other categories: OverrideGraphics with solid fill (legacy Color Splash).
    /// </summary>
    public class ColorSplashEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication uiApp;
        private UIDocument uiDoc => uiApp.ActiveUIDocument;
        private Document doc => uiDoc.Document;

        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public object ColoringResults { get; private set; }

        private string _categoryName;
        private string _parameterName;
        private bool _useGradient;
        private JArray _customColors;
        private readonly Random _random = new Random();

        public void SetParameters(string categoryName, string parameterName, bool useGradient, JArray customColors)
        {
            _categoryName = categoryName;
            _parameterName = parameterName;
            _useGradient = useGradient;
            _customColors = customColors;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            uiApp = uiapp;

            try
            {
                View activeView = doc.ActiveView;
                if (!activeView.CanUseTemporaryVisibilityModes())
                {
                    ColoringResults = new
                    {
                        success = false,
                        message = $"Cannot modify visibility settings in {activeView.ViewType} views"
                    };
                    return;
                }

                Category category = FindCategory(_categoryName);
                if (category == null)
                {
                    ColoringResults = new
                    {
                        success = false,
                        message = $"Category '{_categoryName}' not found"
                    };
                    return;
                }

                bool isRooms =
                    category.Id.GetValue() == (long)BuiltInCategory.OST_Rooms
                    || category.Name.Equals("Rooms", StringComparison.OrdinalIgnoreCase)
                    || category.Name.Equals("Помещения", StringComparison.OrdinalIgnoreCase);

                if (isRooms)
                {
                    ColoringResults = ApplyRoomColorFillScheme(activeView, category);
                }
                else
                {
                    ColoringResults = ApplyOverrideSplash(activeView, category);
                }
            }
            catch (Exception ex)
            {
                ColoringResults = new
                {
                    success = false,
                    message = $"Error: {ex.Message}"
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            // Do not Reset here - SetParameters/Prepare already Reset before Raise.
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName() => "Color Splash";

        private Category FindCategory(string name)
        {
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return cat;
                }
            }
            return null;
        }

        /// <summary>
        /// True room area fill via ColorFillScheme (View → Color Scheme).
        /// </summary>
        private object ApplyRoomColorFillScheme(View activeView, Category roomCategory)
        {
            var rooms = new FilteredElementCollector(doc, activeView.Id)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Autodesk.Revit.DB.Architecture.Room>()
                .Where(r => r.Area > 0)
                .ToList();

            if (rooms.Count == 0)
            {
                return new
                {
                    success = false,
                    message = "No rooms found in the current view"
                };
            }

            // Group by parameter value
            var groups = new Dictionary<string, List<ElementId>>(StringComparer.OrdinalIgnoreCase);
            foreach (var room in rooms)
            {
                string value = GetRoomParameterString(room, _parameterName);
                if (!groups.ContainsKey(value))
                {
                    groups[value] = new List<ElementId>();
                }
                groups[value].Add(room.Id);
            }

            var colorMap = GenerateColors(groups.Keys.ToList());
            ElementId solidFillId = GetSolidFillPatternId();
            if (solidFillId == ElementId.InvalidElementId)
            {
                return new
                {
                    success = false,
                    message = "Solid fill pattern not found — cannot build color scheme entries"
                };
            }

            // Need an existing room ColorFillScheme to duplicate (API has no public ctor)
            ColorFillScheme template = new FilteredElementCollector(doc)
                .OfClass(typeof(ColorFillScheme))
                .Cast<ColorFillScheme>()
                .FirstOrDefault(s =>
                    s.CategoryId == roomCategory.Id
                    || s.CategoryId.GetValue() == (long)BuiltInCategory.OST_Rooms);

            if (template == null)
            {
                return new
                {
                    success = false,
                    message =
                        "В проекте нет цветовой схемы для помещений (ColorFillScheme). " +
                        "Создайте любую: Вид → Цветовая схема → Помещения, затем повторите."
                };
            }

            string schemeName = $"MCP НК {_parameterName} {DateTime.Now:HHmmss}";
            List<object> results;

            using (var tx = new Transaction(doc, "Цветовая схема помещений (MCP)"))
            {
                tx.Start();

                ElementId newId = template.Duplicate(schemeName);
                var scheme = doc.GetElement(newId) as ColorFillScheme;
                if (scheme == null)
                {
                    tx.RollBack();
                    return new { success = false, message = "Failed to duplicate ColorFillScheme" };
                }

                // Prefer single-value entries (by parameter value, not ranges)
                try
                {
                    if (scheme.IsByRange)
                    {
                        scheme.IsByRange = false;
                    }
                }
                catch
                {
                    // Some templates lock IsByRange — continue with entries we can set
                }

                // Point scheme at the same parameter we grouped by (Comments / Name / …)
                TrySetSchemeParameter(scheme, rooms[0], _parameterName);

                var entries = new List<ColorFillSchemeEntry>();
                StorageType storage = GuessStorageType(rooms[0], _parameterName);

                foreach (var group in groups)
                {
                    int[] rgb = colorMap[group.Key];
                    var entry = CreateEntry(storage, group.Key, rgb, solidFillId);
                    if (entry != null)
                    {
                        entries.Add(entry);
                    }
                }

                if (entries.Count == 0)
                {
                    tx.RollBack();
                    return new
                    {
                        success = false,
                        message = $"Could not build ColorFillScheme entries for parameter '{_parameterName}'"
                    };
                }

                scheme.SetEntries(entries);

                ElementId catId = new ElementId(BuiltInCategory.OST_Rooms);
                if (!activeView.CanApplyColorFillScheme(catId, scheme.Id))
                {
                    tx.RollBack();
                    return new
                    {
                        success = false,
                        message =
                            "Активный вид не принимает цветовую схему помещений. " +
                            "Откройте план этажа (не 3D / не лист)."
                    };
                }

                activeView.SetColorFillSchemeId(catId, scheme.Id);

                results = groups.Select(g => (object)new
                {
                    parameterValue = g.Key,
                    count = g.Value.Count,
                    color = new { r = colorMap[g.Key][0], g = colorMap[g.Key][1], b = colorMap[g.Key][2] },
                    elementIds = g.Value.Select(id => id.GetValue().ToString()).ToList()
                }).ToList();

                tx.Commit();
            }

            return new
            {
                success = true,
                mode = "color_fill_scheme",
                schemeName,
                parameterName = _parameterName,
                totalElements = rooms.Count,
                coloredGroups = groups.Count,
                message =
                    "Применена цветовая схема вида (Цветовая область помещений). " +
                    "Если заливка не видна: Свойства вида → Цветовая схема → Помещения.",
                results
            };
        }

        private void TrySetSchemeParameter(
            ColorFillScheme scheme,
            Autodesk.Revit.DB.Architecture.Room sampleRoom,
            string parameterName)
        {
            Parameter param = FindParameter(sampleRoom, parameterName);
            if (param?.Definition == null)
            {
                return;
            }

            try
            {
                // Built-in / shared: ParameterDefinition is ElementId of the definition holder
                if (param.Id != ElementId.InvalidElementId)
                {
                    scheme.ParameterDefinition = param.Id;
                    return;
                }
            }
            catch
            {
                // ignore — keep template parameter
            }

            try
            {
                if (param.Definition is InternalDefinition internalDef)
                {
                    BuiltInParameter bip = internalDef.BuiltInParameter;
                    if (bip != BuiltInParameter.INVALID)
                    {
                        scheme.ParameterDefinition = new ElementId(bip);
                    }
                }
            }
            catch
            {
                // keep template parameter
            }
        }

        private static StorageType GuessStorageType(
            Autodesk.Revit.DB.Architecture.Room room,
            string parameterName)
        {
            Parameter param = FindParameter(room, parameterName);
            return param?.StorageType ?? StorageType.String;
        }

        private static ColorFillSchemeEntry CreateEntry(
            StorageType storage,
            string valueKey,
            int[] rgb,
            ElementId solidFillId)
        {
            try
            {
                var entry = new ColorFillSchemeEntry(storage);
                entry.Color = new Color(
                    (byte)Math.Max(0, Math.Min(255, rgb[0])),
                    (byte)Math.Max(0, Math.Min(255, rgb[1])),
                    (byte)Math.Max(0, Math.Min(255, rgb[2])));
                entry.FillPatternId = solidFillId;
                entry.IsVisible = true;

                string caption = string.IsNullOrWhiteSpace(valueKey) || valueKey == "None"
                    ? "(пусто)"
                    : valueKey;
                entry.Caption = caption.Length > 60 ? caption.Substring(0, 60) : caption;

                switch (storage)
                {
                    case StorageType.String:
                        entry.SetStringValue(
                            valueKey == "None" || valueKey == "(пусто)" ? string.Empty : valueKey);
                        break;
                    case StorageType.Integer:
                        if (int.TryParse(valueKey, out int iv))
                        {
                            entry.SetIntegerValue(iv);
                        }
                        else
                        {
                            entry.SetStringValue(valueKey);
                        }
                        break;
                    case StorageType.Double:
                        if (double.TryParse(
                                valueKey.Replace(',', '.'),
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out double dv))
                        {
                            entry.SetDoubleValue(dv);
                        }
                        else
                        {
                            return null;
                        }
                        break;
                    default:
                        entry.SetStringValue(valueKey == "None" ? string.Empty : valueKey);
                        break;
                }

                return entry;
            }
            catch
            {
                return null;
            }
        }

        private static Parameter FindParameter(Element element, string parameterName)
        {
            Parameter parameter = element.LookupParameter(parameterName);
            if (parameter != null)
            {
                return parameter;
            }

            ElementId typeId = element.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                Element elementType = element.Document.GetElement(typeId);
                parameter = elementType?.LookupParameter(parameterName);
            }

            return parameter;
        }

        private string GetRoomParameterString(
            Autodesk.Revit.DB.Architecture.Room room,
            string parameterName)
        {
            Parameter parameter = FindParameter(room, parameterName);
            if (parameter == null || !parameter.HasValue)
            {
                return "None";
            }
            return GetParameterValueAsString(parameter);
        }

        /// <summary>
        /// Legacy path: OverrideGraphics solid fill (walls/doors/etc.).
        /// Does NOT produce View Color Scheme for rooms.
        /// </summary>
        private object ApplyOverrideSplash(View activeView, Category category)
        {
            FilteredElementCollector collector = new FilteredElementCollector(doc, activeView.Id)
                .OfCategoryId(category.Id)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent();

            ICollection<Element> elements = collector.ToElements();
            if (elements.Count == 0)
            {
                return new
                {
                    success = false,
                    message = $"No elements of category '{_categoryName}' found in the current view"
                };
            }

            var parameterValueGroups = new Dictionary<string, List<ElementId>>();
            foreach (Element element in elements)
            {
                Parameter parameter = FindParameter(element, _parameterName);
                string paramValue = parameter != null && parameter.HasValue
                    ? GetParameterValueAsString(parameter)
                    : "None";

                if (!parameterValueGroups.ContainsKey(paramValue))
                {
                    parameterValueGroups[paramValue] = new List<ElementId>();
                }
                parameterValueGroups[paramValue].Add(element.Id);
            }

            if (parameterValueGroups.Count == 0)
            {
                return new
                {
                    success = false,
                    message = $"No elements with parameter '{_parameterName}' found"
                };
            }

            Dictionary<string, int[]> colorMap = GenerateColors(parameterValueGroups.Keys.ToList());

            using (Transaction transaction = new Transaction(doc, "Color Splash"))
            {
                transaction.Start();

                ElementId solidFillPatternId = GetSolidFillPatternId();
                List<object> coloringResults = new List<object>();

                foreach (var group in parameterValueGroups)
                {
                    string paramValue = group.Key;
                    List<ElementId> elementIds = group.Value;
                    int[] rgb = colorMap[paramValue];

                    OverrideGraphicSettings overrides = new OverrideGraphicSettings();
                    Color color = new Color((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]);
                    overrides.SetProjectionLineColor(color);
                    overrides.SetSurfaceForegroundPatternColor(color);
                    overrides.SetCutForegroundPatternColor(color);

                    if (solidFillPatternId != ElementId.InvalidElementId)
                    {
                        overrides.SetSurfaceForegroundPatternId(solidFillPatternId);
                        overrides.SetCutForegroundPatternId(solidFillPatternId);
                    }

                    foreach (ElementId id in elementIds)
                    {
                        activeView.SetElementOverrides(id, overrides);
                    }

                    coloringResults.Add(new
                    {
                        parameterValue = paramValue,
                        count = elementIds.Count,
                        color = new { r = rgb[0], g = rgb[1], b = rgb[2] },
                        elementIds = elementIds.Select(id => id.GetValue().ToString()).ToList()
                    });
                }

                transaction.Commit();

                return new
                {
                    success = true,
                    mode = "override_graphics",
                    totalElements = elements.Count,
                    coloredGroups = parameterValueGroups.Count,
                    results = coloringResults
                };
            }
        }

        private string GetParameterValueAsString(Parameter parameter)
        {
            if (!parameter.HasValue)
                return "None";

            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    return parameter.AsValueString() ?? parameter.AsDouble().ToString();

                case StorageType.ElementId:
                    ElementId id = parameter.AsElementId();
                    if (id == ElementId.InvalidElementId)
                        return "None";
                    Element element = doc.GetElement(id);
                    return element?.Name ?? id.GetValue().ToString();

                case StorageType.Integer:
                    return parameter.AsValueString() ?? parameter.AsInteger().ToString();

                case StorageType.String:
                    return parameter.AsString() ?? "None";

                default:
                    return "None";
            }
        }

        private Dictionary<string, int[]> GenerateColors(List<string> paramValues)
        {
            Dictionary<string, int[]> colorMap = new Dictionary<string, int[]>();

            if (_customColors != null && _customColors.Count > 0)
            {
                int colorIndex = 0;
                foreach (string value in paramValues)
                {
                    if (colorIndex < _customColors.Count)
                    {
                        JToken colorToken = _customColors[colorIndex];
                        if (colorToken["r"] != null && colorToken["g"] != null && colorToken["b"] != null)
                        {
                            colorMap[value] = new int[]
                            {
                                colorToken["r"].ToObject<int>(),
                                colorToken["g"].ToObject<int>(),
                                colorToken["b"].ToObject<int>()
                            };
                        }
                        else
                        {
                            colorMap[value] = GenerateRandomColor();
                        }
                    }
                    else
                    {
                        colorMap[value] = GenerateRandomColor();
                    }
                    colorIndex++;
                }
            }
            else if (_useGradient && paramValues.Count > 1)
            {
                int[] startColor = new int[] { 0, 0, 180 };
                int[] endColor = new int[] { 180, 0, 0 };

                for (int i = 0; i < paramValues.Count; i++)
                {
                    double ratio = (double)i / (paramValues.Count - 1);
                    colorMap[paramValues[i]] = new int[]
                    {
                        (int)(startColor[0] + (endColor[0] - startColor[0]) * ratio),
                        (int)(startColor[1] + (endColor[1] - startColor[1]) * ratio),
                        (int)(startColor[2] + (endColor[2] - startColor[2]) * ratio)
                    };
                }
            }
            else
            {
                foreach (string value in paramValues)
                {
                    // Make "НК-нарушение" / violation-like keys vivid red by default
                    if (value.IndexOf("нарушен", StringComparison.OrdinalIgnoreCase) >= 0
                        || value.IndexOf("violation", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        colorMap[value] = new[] { 255, 0, 0 };
                    }
                    else if (value == "None" || string.IsNullOrWhiteSpace(value))
                    {
                        colorMap[value] = new[] { 220, 220, 220 };
                    }
                    else
                    {
                        colorMap[value] = GenerateRandomColor();
                    }
                }
            }

            return colorMap;
        }

        private int[] GenerateRandomColor()
        {
            return new int[]
            {
                _random.Next(30, 200),
                _random.Next(30, 200),
                _random.Next(30, 200)
            };
        }

        private ElementId GetSolidFillPatternId()
        {
            FilteredElementCollector collector = new FilteredElementCollector(doc);
            collector.OfClass(typeof(FillPatternElement));

            foreach (FillPatternElement patternElement in collector)
            {
                FillPattern pattern = patternElement.GetFillPattern();
                if (pattern.IsSolidFill)
                {
                    return patternElement.Id;
                }
            }

            return ElementId.InvalidElementId;
        }
    }
}
