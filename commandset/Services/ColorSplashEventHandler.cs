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
#if REVIT2022_OR_GREATER
                    ColoringResults = ApplyRoomColorFillScheme(activeView, category);
#else
                    // ColorFillScheme завели только в API Revit 2022. Раскрасить помещения
                    // переопределениями вида на 2020-2021 можно, но это не цветовая схема:
                    // легенда останется пустой, а картинка будет похожа ровно настолько,
                    // чтобы подмену никто не заметил. Отказываем вслух.
                    ColoringResults = new
                    {
                        success = false,
                        message =
                            "Цветовая схема помещений доступна начиная с Revit 2022. " +
                            "В этой версии раскрасить помещения по параметру нечем."
                    };
#endif
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

#if REVIT2022_OR_GREATER
        /// <summary>
        /// True room area fill via ColorFillScheme (View → Color Scheme).
        /// </summary>
        private object ApplyRoomColorFillScheme(View activeView, Category roomCategory)
        {
            var allRooms = new FilteredElementCollector(doc, activeView.Id)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .Cast<Autodesk.Revit.DB.Architecture.Room>()
                .ToList();

            var rooms = allRooms.Where(r => r.Area > 0).ToList();

            // Rooms Revit gives no area to: an unclosed outline, or a second room
            // sharing an enclosure with another («Избыточная Помещение»). They were
            // filtered out silently, and that silence is the bug: Revit refuses to
            // run the colour fill calculation for the view while they exist —
            // «Не удалось выполнить расчёт Цветовая заливка», in a background-process
            // dialog the tool never sees — and paints every room with one fallback
            // hatch. The scheme applies, every read-back passes, and the answer says
            // the plan is coloured while the architect is looking at flat pink
            // (19–20.08.2026).
            var unpaintable = allRooms
                .Where(r => r.Area <= 0)
                .Select(r => string.IsNullOrWhiteSpace(r.Number) ? r.Id.GetValue().ToString() : r.Number)
                .ToList();

            if (rooms.Count == 0)
            {
                return new
                {
                    success = false,
                    message = unpaintable.Count > 0
                        ? $"В виде {unpaintable.Count} помещ. без площади (№{string.Join(", №", unpaintable.Take(15))}) "
                          + "и ни одного пригодного: контуры не замкнуты либо помещения избыточные. Красить нечего."
                        : "No rooms found in the current view"
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

            // Need an existing room ColorFillScheme to duplicate (API has no public ctor).
            // Prefer one that is not by-range: some templates refuse to leave range
            // mode, and a range scheme pointed at a text parameter is exactly the
            // state Revit cannot compute a fill for.
            var roomSchemes = new FilteredElementCollector(doc)
                .OfClass(typeof(ColorFillScheme))
                .Cast<ColorFillScheme>()
                .Where(s =>
                    s.CategoryId == roomCategory.Id
                    || s.CategoryId.GetValue() == (long)BuiltInCategory.OST_Rooms)
                .ToList();

            ColorFillScheme template =
                roomSchemes.FirstOrDefault(s => !s.IsByRange) ?? roomSchemes.FirstOrDefault();

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
            int coloredRooms;
            List<string> skippedValues;

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

                // Prefer single-value entries (by parameter value, not ranges).
                // Carrying on after this fails used to be the whole bug: the scheme
                // stayed in range mode, Revit could not reconcile it with a text
                // parameter, and the tool still answered "36 помещений, у каждого
                // свой цвет" over a plan Revit had refused to colour (19.08.2026).
                try
                {
                    if (scheme.IsByRange)
                    {
                        scheme.IsByRange = false;
                    }
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    return new
                    {
                        success = false,
                        message =
                            $"Цветовая схема «{template.Name}» не выходит из режима диапазонов " +
                            $"({ex.Message}). Создайте схему по значению: Вид → Цветовая схема → " +
                            "Помещения → по параметру, затем повторите."
                    };
                }

                // Point scheme at the same parameter we grouped by (Comments / Name / …)
                if (!TryPointSchemeAtParameter(scheme, rooms[0], _parameterName, out string repointError))
                {
                    tx.RollBack();
                    return new
                    {
                        success = false,
                        message =
                            $"Не удалось построить цветовую схему по параметру «{_parameterName}»: " +
                            $"{repointError} Раскраска не применена — вид остался как был."
                    };
                }

                var entries = new List<ColorFillSchemeEntry>();
                // Which groups actually got an entry. CreateEntry returns null on a
                // value Revit will not take, and dropping those silently is how the
                // count in the answer stopped matching the colours on the plan.
                var entered = new List<string>();
                var skipped = new List<string>();
                StorageType storage = GuessStorageType(rooms[0], _parameterName);

                foreach (var group in groups)
                {
                    int[] rgb = colorMap[group.Key];
                    var entry = CreateEntry(storage, group.Key, rgb, solidFillId);
                    if (entry != null)
                    {
                        entries.Add(entry);
                        entered.Add(group.Key);
                    }
                    else
                    {
                        skipped.Add(group.Key);
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

                // Read back what Revit actually stored. Every setter above can be
                // quietly overruled by the template, and a scheme whose entries and
                // whose parameter disagree is the state that renders as one flat
                // hatch while the tool reports a colour per room.
                if (scheme.IsByRange)
                {
                    tx.RollBack();
                    return new
                    {
                        success = false,
                        message =
                            "Схема осталась в режиме диапазонов — Revit не рассчитает по ней заливку. " +
                            "Раскраска не применена."
                    };
                }

                if (activeView.GetColorFillSchemeId(catId) != scheme.Id)
                {
                    tx.RollBack();
                    return new
                    {
                        success = false,
                        message =
                            "Вид не принял цветовую схему помещений. Раскраска не применена."
                    };
                }

                // Report the groups that made it into the scheme, not the ones we
                // hoped for.
                results = entered.Select(key => (object)new
                {
                    parameterValue = key,
                    count = groups[key].Count,
                    color = new { r = colorMap[key][0], g = colorMap[key][1], b = colorMap[key][2] },
                    elementIds = groups[key].Select(id => id.GetValue().ToString()).ToList()
                }).ToList();

                coloredRooms = entered.Sum(key => groups[key].Count);
                skippedValues = skipped;

                tx.Commit();
            }

            string message;

            if (unpaintable.Count > 0)
            {
                // First sentence on purpose: this is the reason the plan will look
                // wrong, and it has to reach the architect ahead of the good news.
                message =
                    $"ЗАЛИВКА НЕ РАССЧИТАЕТСЯ: в виде {unpaintable.Count} помещ. без площади "
                    + $"(№{string.Join(", №", unpaintable.Take(15))}"
                    + (unpaintable.Count > 15 ? " и др." : "") + "). "
                    + "Пока в модели есть избыточные помещения или незамкнутые контуры, Revit "
                    + "отказывается считать «Цветовую заливку» и красит весь план одной штриховкой "
                    + "вместо цветов. Сначала замкните контуры / удалите избыточные помещения, "
                    + "затем повторите раскраску. ";
            }
            else
            {
                message = "";
            }

            message +=
                "Применена цветовая схема вида (Цветовая область помещений). " +
                "Если заливка не видна: Свойства вида → Цветовая схема → Помещения.";

            if (skippedValues.Count > 0)
            {
                message +=
                    $" Без цвета осталось {rooms.Count - coloredRooms} помещ. — " +
                    $"Revit не принял значения: {string.Join(", ", skippedValues.Take(10))}" +
                    (skippedValues.Count > 10 ? " и др." : "") + ".";
            }

            return new
            {
                success = true,
                mode = "color_fill_scheme",
                schemeName,
                parameterName = _parameterName,
                totalElements = rooms.Count,
                coloredElements = coloredRooms,
                coloredGroups = results.Count,
                skippedValues,
                // Named separately so a caller can act on it without parsing prose.
                roomsWithoutArea = unpaintable,
                colorFillBlocked = unpaintable.Count > 0,
                message,
                results
            };
        }

        /// <summary>
        /// Point the scheme at the parameter the rooms were grouped by, and say so
        /// when it cannot be done.
        ///
        /// This used to be void and swallow both attempts. When it failed the scheme
        /// kept the template's parameter — «Имя», say — while the entries held room
        /// numbers, so no room matched any entry: Revit reported «Не удалось
        /// выполнить расчёт Цветовая заливка» in a background dialog and the tool,
        /// which never looked, reported a colour per room.
        /// </summary>
        private bool TryPointSchemeAtParameter(
            ColorFillScheme scheme,
            Autodesk.Revit.DB.Architecture.Room sampleRoom,
            string parameterName,
            out string error)
        {
            error = null;

            Parameter param = FindParameter(sampleRoom, parameterName);
            if (param?.Definition == null)
            {
                error = $"у помещений нет параметра «{parameterName}».";
                return false;
            }

            ElementId wanted = param.Id;
            if (wanted == ElementId.InvalidElementId
                && param.Definition is InternalDefinition internalDef
                && internalDef.BuiltInParameter != BuiltInParameter.INVALID)
            {
                wanted = new ElementId(internalDef.BuiltInParameter);
            }

            if (wanted == ElementId.InvalidElementId)
            {
                error = $"параметр «{parameterName}» нельзя использовать в цветовой схеме.";
                return false;
            }

            try
            {
                scheme.ParameterDefinition = wanted;
            }
            catch (Exception ex)
            {
                error = $"Revit отказал в параметре схемы ({ex.Message}).";
                return false;
            }

            // The setter can be accepted and then overruled by the template; the
            // only trustworthy answer is what the scheme reports afterwards.
            if (scheme.ParameterDefinition != wanted)
            {
                error =
                    $"схема осталась на параметре шаблона вместо «{parameterName}» — " +
                    "цвета не совпали бы с помещениями.";
                return false;
            }

            return true;
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
#endif

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

        private ElementId GetSolidFillPatternId() => SolidFillPatterns.FindId(doc);
    }
}
