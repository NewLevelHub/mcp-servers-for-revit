using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class GetAvailableFamilyTypesEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        /// <summary>
        /// Paged envelope, not a bare list. The bare list was cut to Limit without a
        /// word, so a project with 300 door types answered with 100 and no sign that
        /// the rest existed — the model then picked from an arbitrary prefix.
        /// </summary>
        public AIResult<List<FamilyTypeInfo>> Result { get; private set; }

        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public List<string> CategoryList { get; set; }
        public string FamilyNameFilter { get; set; }
        public int? Limit { get; set; }
        public int? Offset { get; set; }

        /// <summary>
        /// Reset wait state before ExternalEvent.Raise. Must be called from the command before RaiseAndWaitForCompletion.
        /// </summary>
        public void Prepare()
        {
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 12500)
        {
            // Do not Reset here - SetParameters/Prepare already Reset before Raise.
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument.Document;
                int limit = Limit.HasValue && Limit.Value > 0 ? Limit.Value : int.MaxValue;
                int offset = Offset.HasValue && Offset.Value > 0 ? Offset.Value : 0;

                var categoryIds = ResolveCategoryIds(CategoryList);
                bool filterByCategory = categoryIds.Count > 0;

                // Every match is collected before paging: the total is the whole point
                // of the envelope, and it cannot be known while short-circuiting on
                // limit. These are types, not instances — the count stays in the
                // hundreds even on a large model.
                var matches = new List<FamilyTypeInfo>();

                // Loadable families — apply multicategory filter in Revit when possible.
                FilteredElementCollector symbolCollector = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol));
                if (filterByCategory)
                {
                    symbolCollector = symbolCollector.WherePasses(
                        new ElementMulticategoryFilter(categoryIds));
                }

                foreach (FamilySymbol symbol in symbolCollector.Cast<FamilySymbol>())
                {
                    if (!MatchesNameFilter(symbol.FamilyName, symbol.Name))
                        continue;
                    matches.Add(ToFamilyTypeInfo(symbol, symbol.FamilyName));
                }

                // System types: only collect classes that can match requested categories (or all if unfiltered).
                foreach (ElementType systemType in EnumerateSystemTypes(doc, categoryIds, filterByCategory))
                {
                    if (filterByCategory && !CategoryMatches(systemType, categoryIds))
                        continue;

                    string familyName = systemType.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM)?.AsString()
                        ?? systemType.GetType().Name.Replace("Type", "");

                    if (!MatchesNameFilter(familyName, systemType.Name))
                        continue;

                    matches.Add(ToFamilyTypeInfo(systemType, familyName));
                }

                var page = matches.Skip(offset).Take(limit).ToList();
                var hasMore = offset + page.Count < matches.Count;

                Result = new AIResult<List<FamilyTypeInfo>>
                {
                    // Nothing matching a filter is a valid answer, not a refusal —
                    // toolOutcome must not turn an empty catalogue into an error.
                    Success = true,
                    Message = BuildMessage(matches.Count, page.Count, offset, hasMore),
                    Response = page,
                    TotalCount = matches.Count,
                    HasMore = hasMore,
                    Offset = offset,
                    Limit = limit == int.MaxValue ? (int?)null : limit
                };
            }
            catch (Exception ex)
            {
                // No TaskDialog.Show: this runs inside an ExternalEvent with nobody able
                // to click it during an agent-driven turn — it would hang the chat.
                System.Diagnostics.Trace.WriteLine($"get_available_family_types failed: {ex}");
                Result = new AIResult<List<FamilyTypeInfo>>
                {
                    Success = false,
                    Message = ex.Message,
                    Response = new List<FamilyTypeInfo>()
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        /// <summary>
        /// Says outright when the list is a page, and what to do about it. Silent
        /// truncation is what made the model treat a prefix as the whole catalogue.
        /// </summary>
        private static string BuildMessage(int total, int shown, int offset, bool hasMore)
        {
            if (total == 0)
                return "Подходящих типов не найдено. Проверьте categoryList / familyNameFilter.";

            if (!hasMore && offset == 0)
                return $"Найдено типов: {total} (показаны все).";

            return $"Найдено типов: {total}, показано {shown} начиная с {offset}. "
                   + (hasMore
                       ? $"Есть ещё: повторите с offset={offset + shown} или сузьте categoryList / familyNameFilter."
                       : "Это последняя страница.");
        }

        private bool MatchesNameFilter(string familyName, string typeName)
        {
            if (string.IsNullOrEmpty(FamilyNameFilter))
                return true;

            return (familyName?.IndexOf(FamilyNameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                || (typeName?.IndexOf(FamilyNameFilter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static List<BuiltInCategory> ResolveCategoryIds(List<string> categoryList)
        {
            var ids = new List<BuiltInCategory>();
            if (categoryList == null)
                return ids;

            foreach (var categoryName in categoryList)
            {
                if (Enum.TryParse(categoryName, out BuiltInCategory bic)
                    && bic != BuiltInCategory.INVALID)
                {
                    ids.Add(bic);
                }
            }

            return ids;
        }

        private static bool CategoryMatches(ElementType elementType, List<BuiltInCategory> categoryIds)
        {
            if (elementType.Category == null)
                return false;

#if REVIT2024_OR_GREATER
            var categoryId = elementType.Category.Id.Value;
#else
            var categoryId = elementType.Category.Id.IntegerValue;
#endif
            foreach (var bic in categoryIds)
            {
                if ((int)bic == (int)categoryId)
                    return true;
            }

            return false;
        }

        private static IEnumerable<ElementType> EnumerateSystemTypes(
            Document doc,
            List<BuiltInCategory> categoryIds,
            bool filterByCategory)
        {
            bool Want(BuiltInCategory category)
            {
                if (!filterByCategory)
                    return true;
                return categoryIds.Contains(category);
            }

            if (Want(BuiltInCategory.OST_Walls))
            {
                foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<ElementType>())
                    yield return t;
            }

            if (Want(BuiltInCategory.OST_Floors))
            {
                foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<ElementType>())
                    yield return t;
            }

            if (Want(BuiltInCategory.OST_Roofs))
            {
                foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<ElementType>())
                    yield return t;
            }

            if (Want(BuiltInCategory.OST_Ceilings))
            {
                foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).Cast<ElementType>())
                    yield return t;
            }

            if (Want(BuiltInCategory.OST_CurtaSystem))
            {
                foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(CurtainSystemType)).Cast<ElementType>())
                    yield return t;
            }

            if (Want(BuiltInCategory.OST_Stairs))
            {
                foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(StairsType)).Cast<ElementType>())
                    yield return t;
            }

            if (Want(BuiltInCategory.OST_StairsRailing))
            {
                foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(RailingType)).Cast<ElementType>())
                    yield return t;
            }
        }

        private static FamilyTypeInfo ToFamilyTypeInfo(ElementType et, string familyName)
        {
            return new FamilyTypeInfo
            {
#if REVIT2024_OR_GREATER
                FamilyTypeId = et.Id.Value,
#else
                FamilyTypeId = et.Id.IntegerValue,
#endif
                UniqueId = et.UniqueId,
                FamilyName = familyName,
                TypeName = et.Name,
                Category = et.Category?.Name
            };
        }

        public string GetName()
        {
            return "GetAvailableFamilyTypes";
        }
    }
}
