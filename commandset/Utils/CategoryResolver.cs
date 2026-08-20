using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils;

/// <summary>
///     Resolve the category a caller named, whatever language the Revit UI runs in.
///
///     <para>
///     <c>doc.Settings.Categories</c> holds <b>localised</b> names: the same category
///     is "Помещения" in a Russian Revit and "Rooms" in an English one. Matching the
///     incoming string against that collection therefore works only when the caller
///     guessed the session language. On 20.08.2026 the same model, same project and
///     same request failed at 12:47 and succeeded at 11:02 for exactly that reason —
///     Revit had been restarted in English in between, and <c>color_elements</c>
///     answered "Category 'Помещения' not found". The model then burned two more
///     rounds guessing.
///     </para>
///
///     <para>
///     So the localised scan is the last resort here, not the first: an alias table
///     and the <c>OST_*</c> enum name both resolve without knowing the language.
///     </para>
/// </summary>
public static class CategoryResolver
{
    /// <summary>
    ///     Russian and bare-English names for the categories that actually get asked
    ///     for. Kept in step with <c>server/src/utils/revitCategories.ts</c>, which
    ///     does the same job for category *filters*; this one is for the category a
    ///     command operates on.
    /// </summary>
    private static readonly Dictionary<string, BuiltInCategory> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "Rooms", BuiltInCategory.OST_Rooms },
            { "Room", BuiltInCategory.OST_Rooms },
            { "Помещения", BuiltInCategory.OST_Rooms },
            { "Помещение", BuiltInCategory.OST_Rooms },
            { "Комнаты", BuiltInCategory.OST_Rooms },
            { "Areas", BuiltInCategory.OST_Areas },
            { "Зоны", BuiltInCategory.OST_Areas },
            { "Walls", BuiltInCategory.OST_Walls },
            { "Wall", BuiltInCategory.OST_Walls },
            { "Стены", BuiltInCategory.OST_Walls },
            { "Стена", BuiltInCategory.OST_Walls },
            { "Doors", BuiltInCategory.OST_Doors },
            { "Door", BuiltInCategory.OST_Doors },
            { "Двери", BuiltInCategory.OST_Doors },
            { "Дверь", BuiltInCategory.OST_Doors },
            { "Windows", BuiltInCategory.OST_Windows },
            { "Window", BuiltInCategory.OST_Windows },
            { "Окна", BuiltInCategory.OST_Windows },
            { "Окно", BuiltInCategory.OST_Windows },
            { "Floors", BuiltInCategory.OST_Floors },
            { "Перекрытия", BuiltInCategory.OST_Floors },
            { "Полы", BuiltInCategory.OST_Floors },
            { "Ceilings", BuiltInCategory.OST_Ceilings },
            { "Потолки", BuiltInCategory.OST_Ceilings },
            { "Roofs", BuiltInCategory.OST_Roofs },
            { "Крыши", BuiltInCategory.OST_Roofs },
            { "Кровля", BuiltInCategory.OST_Roofs },
            { "Furniture", BuiltInCategory.OST_Furniture },
            { "Мебель", BuiltInCategory.OST_Furniture },
            { "Columns", BuiltInCategory.OST_Columns },
            { "Колонны", BuiltInCategory.OST_Columns },
            { "Structural Columns", BuiltInCategory.OST_StructuralColumns },
            { "Несущие колонны", BuiltInCategory.OST_StructuralColumns },
            { "Structural Framing", BuiltInCategory.OST_StructuralFraming },
            { "Балки", BuiltInCategory.OST_StructuralFraming },
            { "Stairs", BuiltInCategory.OST_Stairs },
            { "Лестницы", BuiltInCategory.OST_Stairs },
            { "Railings", BuiltInCategory.OST_StairsRailing },
            { "Ограждения", BuiltInCategory.OST_StairsRailing },
            { "Grids", BuiltInCategory.OST_Grids },
            { "Оси", BuiltInCategory.OST_Grids },
            { "Levels", BuiltInCategory.OST_Levels },
            { "Уровни", BuiltInCategory.OST_Levels },
            { "Generic Models", BuiltInCategory.OST_GenericModel },
            { "Обобщенные модели", BuiltInCategory.OST_GenericModel },
            { "Casework", BuiltInCategory.OST_Casework },
            { "Plumbing Fixtures", BuiltInCategory.OST_PlumbingFixtures },
            { "Сантехника", BuiltInCategory.OST_PlumbingFixtures },
            { "Lighting Fixtures", BuiltInCategory.OST_LightingFixtures },
            { "Светильники", BuiltInCategory.OST_LightingFixtures },
            { "Speciality Equipment", BuiltInCategory.OST_SpecialityEquipment },
            // Смежники (REV-167). check_link_clashes lets the caller name the trade
            // categories it should look at, and «Воздуховоды» must not miss for the
            // same reason «Помещения» must not: the UI language is not the caller's
            // to know.
            { "Structural Foundations", BuiltInCategory.OST_StructuralFoundation },
            { "Фундаменты", BuiltInCategory.OST_StructuralFoundation },
            { "Ducts", BuiltInCategory.OST_DuctCurves },
            { "Воздуховоды", BuiltInCategory.OST_DuctCurves },
            { "Duct Fittings", BuiltInCategory.OST_DuctFitting },
            { "Соединительные детали воздуховодов", BuiltInCategory.OST_DuctFitting },
            { "Pipes", BuiltInCategory.OST_PipeCurves },
            { "Трубы", BuiltInCategory.OST_PipeCurves },
            { "Pipe Fittings", BuiltInCategory.OST_PipeFitting },
            { "Соединительные детали трубопроводов", BuiltInCategory.OST_PipeFitting },
            { "Cable Trays", BuiltInCategory.OST_CableTray },
            { "Кабельные лотки", BuiltInCategory.OST_CableTray },
            { "Conduits", BuiltInCategory.OST_Conduit },
            { "Короба", BuiltInCategory.OST_Conduit },
            { "Mechanical Equipment", BuiltInCategory.OST_MechanicalEquipment },
            { "Оборудование ОВ", BuiltInCategory.OST_MechanicalEquipment },
            { "Specialty Equipment", BuiltInCategory.OST_SpecialityEquipment },
            { "Оборудование", BuiltInCategory.OST_SpecialityEquipment },
        };

    /// <summary>The category, or <c>null</c> when the name resolves to nothing.</summary>
    public static Category? Find(Document doc, string name)
    {
        if (doc == null || string.IsNullOrWhiteSpace(name))
            return null;

        var wanted = name.Trim();

        // 1. An alias in either language — language-independent, so it goes first.
        if (Aliases.TryGetValue(wanted, out var alias))
        {
            var byAlias = GetCategory(doc, alias);
            if (byAlias != null)
                return byAlias;
        }

        // 2. The enum name itself: what the MCP layer normalises filters to.
        if (wanted.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<BuiltInCategory>(wanted, true, out var parsed))
        {
            var byEnum = GetCategory(doc, parsed);
            if (byEnum != null)
                return byEnum;
        }

        // 3. The localised name, which is all this used to do.
        foreach (Category category in doc.Settings.Categories)
        {
            if (string.Equals(category.Name, wanted, StringComparison.OrdinalIgnoreCase))
                return category;
        }

        return null;
    }

    /// <summary>
    ///     Why the lookup missed, plus the names this document actually offers.
    ///     A bare "not found" left the model guessing spellings for two rounds.
    /// </summary>
    public static string DescribeMiss(Document doc, string name)
    {
        var message = $"Категория «{name}» не найдена в этом документе";

        if (doc == null)
            return message + ".";

        var needle = (name ?? string.Empty).Trim();
        var suggestions = new List<string>();

        foreach (Category category in doc.Settings.Categories)
        {
            var candidate = category?.Name;
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (candidate.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || needle.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                suggestions.Add(candidate);
            }
        }

        if (suggestions.Count > 0)
        {
            message += ". Похожие: " + string.Join(", ", suggestions.Take(5));
        }
        else
        {
            // Naming the UI language is the actionable part: it tells the caller
            // which spelling of every other category will work too.
            message +=
                ". Имена категорий локализованы — в этом сеансе Revit они выглядят так: "
                + string.Join(", ", SampleNames(doc))
                + ". Можно передавать имя на русском, на английском или как OST_*.";
        }

        return message;
    }

    private static IEnumerable<string> SampleNames(Document doc)
    {
        var wanted = new[]
        {
            BuiltInCategory.OST_Rooms,
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Doors,
        };

        foreach (var bic in wanted)
        {
            var category = GetCategory(doc, bic);
            if (!string.IsNullOrWhiteSpace(category?.Name))
                yield return category!.Name;
        }
    }

    private static Category? GetCategory(Document doc, BuiltInCategory builtIn)
    {
        try
        {
            return Category.GetCategory(doc, builtIn);
        }
        catch
        {
            // Not every BuiltInCategory exists in every document/discipline.
            return null;
        }
    }
}
