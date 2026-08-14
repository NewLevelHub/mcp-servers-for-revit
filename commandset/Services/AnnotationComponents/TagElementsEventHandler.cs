using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.AnnotationComponents;

/// <summary>
///     Real Revit tags for any category (doors, windows, walls, …), by category or by
///     element id. Before this, only <c>tag_rooms</c> and <c>tag_walls</c> existed, so a
///     request to mark doors had no tool behind it and the model substituted red text
///     notes that merely looked like marks.
/// </summary>
public class TagElementsEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private UIApplication _uiApp;
    private Document Doc => _uiApp.ActiveUIDocument.Document;
    private readonly ManualResetEvent _resetEvent = new(false);

    private string _category;
    private List<long> _elementIds;
    private bool _useLeader;
    private string _tagTypeId;
    private int _viewId;

    public AIResult<List<int>> Result { get; private set; }

    public void SetParameters(
        string category,
        List<long> elementIds,
        bool useLeader,
        string tagTypeId,
        int viewId)
    {
        _category = category;
        _elementIds = elementIds ?? new List<long>();
        _useLeader = useLeader;
        _tagTypeId = tagTypeId;
        _viewId = viewId;
        _resetEvent.Reset();
    }

    public void Execute(UIApplication app)
    {
        _uiApp = app;
        try
        {
            var view = _viewId > 0
                ? Doc.GetElement(new ElementId(_viewId)) as View
                : Doc.ActiveView;

            if (view == null)
            {
                Result = Fail("Активный вид не найден.");
                return;
            }

            if (view.ViewType == ViewType.DrawingSheet)
            {
                Result = Fail(
                    "Марки нельзя ставить на листе — откройте план, разрез или фасад и повторите.");
                return;
            }

            var targets = CollectTargets(view, out var collectError);
            if (collectError != null)
            {
                Result = Fail(collectError);
                return;
            }

            if (targets.Count == 0)
            {
                Result = Fail(string.IsNullOrWhiteSpace(_category)
                    ? "Не переданы элементы для маркировки."
                    : $"На виде «{view.Name}» нет элементов категории {_category}.");
                return;
            }

            var alreadyTagged = CollectTaggedElementIds(view);

            var created = new List<int>();
            var skipped = new List<string>();
            var failures = new List<string>();

            using var transaction = new Transaction(Doc, "Маркировка элементов");
            transaction.Start();

            FamilySymbol tagType;
            try
            {
                tagType = ResolveTagType(targets[0]);
            }
            catch (Exception ex)
            {
                transaction.RollBack();
                Result = Fail(ex.Message);
                return;
            }

            if (tagType == null)
            {
                transaction.RollBack();
                Result = Fail(
                    "В проекте нет загруженного семейства марок для этой категории. " +
                    "Загрузите марку (например «Марка двери») через Вставка → Загрузить семейство.");
                return;
            }

            if (!tagType.IsActive)
            {
                tagType.Activate();
                Doc.Regenerate();
            }

            foreach (var element in targets)
            {
                var id = element.Id.GetIntValue();
                if (alreadyTagged.Contains(element.Id.GetValue()))
                {
                    skipped.Add($"{id} (марка уже стоит)");
                    continue;
                }

                var point = GetTagPoint(element);
                if (point == null)
                {
                    failures.Add($"{id}: у элемента нет положения на виде");
                    continue;
                }

                try
                {
                    var tag = IndependentTag.Create(
                        Doc,
                        tagType.Id,
                        view.Id,
                        new Reference(element),
                        _useLeader,
                        TagOrientation.Horizontal,
                        point);

                    if (tag == null)
                        failures.Add($"{id}: Revit не создал марку");
                    else
                        created.Add(tag.Id.GetIntValue());
                }
                catch (Exception ex)
                {
                    failures.Add($"{id}: {ex.Message}");
                }
            }

            transaction.Commit();

            var message = $"Создано марок: {created.Count} из {targets.Count}.";
            if (skipped.Count > 0)
                message += $" Пропущено (уже промаркированы): {skipped.Count}.";
            if (failures.Count > 0)
                message += " Не удалось — " + string.Join("; ", failures) + ".";

            Result = new AIResult<List<int>>
            {
                // Everything already tagged is a legitimate no-op, not a failure.
                Success = created.Count > 0 || skipped.Count == targets.Count,
                Message = message,
                Response = created,
            };
        }
        catch (Exception ex)
        {
            Result = Fail($"Ошибка маркировки: {ex.Message}");
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    private static AIResult<List<int>> Fail(string message) => new()
    {
        Success = false,
        Message = message,
        Response = new List<int>(),
    };

    private List<Element> CollectTargets(View view, out string error)
    {
        error = null;

        if (_elementIds.Count > 0)
        {
            var found = new List<Element>();
            var missing = new List<long>();
            foreach (var rawId in _elementIds)
            {
                var element = Doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(rawId));
                if (element == null)
                    missing.Add(rawId);
                else
                    found.Add(element);
            }

            if (found.Count == 0)
            {
                error = $"Элементы не найдены в модели: {string.Join(", ", missing)}";
                return new List<Element>();
            }

            return found;
        }

        if (string.IsNullOrWhiteSpace(_category))
        {
            error = "Укажите category (например OST_Doors) или список elementIds.";
            return new List<Element>();
        }

        if (!Enum.TryParse(_category.Trim(), true, out BuiltInCategory builtInCategory)
            || builtInCategory == BuiltInCategory.INVALID)
        {
            error =
                $"Категория «{_category}» не распознана. Ожидается имя вида OST_Doors, OST_Windows, OST_Walls.";
            return new List<Element>();
        }

        return new FilteredElementCollector(Doc, view.Id)
            .OfCategory(builtInCategory)
            .WhereElementIsNotElementType()
            .ToList();
    }

    private HashSet<long> CollectTaggedElementIds(View view)
    {
        var tagged = new HashSet<long>();
        var tags = new FilteredElementCollector(Doc, view.Id)
            .OfClass(typeof(IndependentTag))
            .WhereElementIsNotElementType()
            .Cast<IndependentTag>();

        foreach (var tag in tags)
        {
            foreach (var taggedId in tag.GetTaggedLocalElementIds())
                tagged.Add(taggedId.GetValue());
        }

        return tagged;
    }

    /// <summary>Doors and windows sit on a point; walls and beams on a curve.</summary>
    private static XYZ GetTagPoint(Element element)
    {
        switch (element.Location)
        {
            case LocationPoint locationPoint:
                return locationPoint.Point;
            case LocationCurve locationCurve:
                return locationCurve.Curve?.Evaluate(0.5, true);
            default:
                var box = element.get_BoundingBox(null);
                return box == null ? null : (box.Min + box.Max) / 2.0;
        }
    }

    /// <summary>
    ///     Tag family for the target's category: the explicit id when given, then a tag
    ///     whose category matches the element, then a multi-category tag.
    /// </summary>
    private FamilySymbol ResolveTagType(Element sample)
    {
        if (!string.IsNullOrWhiteSpace(_tagTypeId) && long.TryParse(_tagTypeId, out var typeId))
        {
            if (Doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(typeId)) is FamilySymbol explicitSymbol)
                return explicitSymbol;

            throw new ArgumentException(
                $"tagTypeId {_tagTypeId} не является типоразмером марки в этом проекте.");
        }

        var wantedTagCategory = TagCategoryFor(sample);
        var symbols = new FilteredElementCollector(Doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(symbol => symbol.Category != null)
            .ToList();

        if (wantedTagCategory != BuiltInCategory.INVALID)
        {
            var match = symbols.FirstOrDefault(
                symbol => symbol.Category.Id.GetIntValue() == (int)wantedTagCategory);
            if (match != null)
                return match;
        }

        return symbols.FirstOrDefault(
            symbol => symbol.Category.Id.GetIntValue() == (int)BuiltInCategory.OST_MultiCategoryTags);
    }

    private static BuiltInCategory TagCategoryFor(Element element)
    {
        if (element.Category == null)
            return BuiltInCategory.INVALID;

        return (BuiltInCategory)element.Category.Id.GetIntValue() switch
        {
            BuiltInCategory.OST_Doors => BuiltInCategory.OST_DoorTags,
            BuiltInCategory.OST_Windows => BuiltInCategory.OST_WindowTags,
            BuiltInCategory.OST_Walls => BuiltInCategory.OST_WallTags,
            BuiltInCategory.OST_Rooms => BuiltInCategory.OST_RoomTags,
            BuiltInCategory.OST_Floors => BuiltInCategory.OST_FloorTags,
            BuiltInCategory.OST_Ceilings => BuiltInCategory.OST_CeilingTags,
            BuiltInCategory.OST_Furniture => BuiltInCategory.OST_FurnitureTags,
            BuiltInCategory.OST_StructuralColumns => BuiltInCategory.OST_StructuralColumnTags,
            BuiltInCategory.OST_StructuralFraming => BuiltInCategory.OST_StructuralFramingTags,
            BuiltInCategory.OST_Stairs => BuiltInCategory.OST_StairsTags,
            BuiltInCategory.OST_GenericModel => BuiltInCategory.OST_GenericModelTags,
            _ => BuiltInCategory.INVALID,
        };
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 15000)
    {
        // Do not Reset here - SetParameters already Reset before Raise.
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public string GetName() => "Tag Elements";
}
