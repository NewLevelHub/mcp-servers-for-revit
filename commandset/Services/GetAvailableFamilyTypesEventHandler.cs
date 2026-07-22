using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    public class GetAvailableFamilyTypesEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        public List<FamilyTypeInfo> ResultFamilyTypes { get; private set; }

        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public List<string> CategoryList { get; set; }
        public string FamilyNameFilter { get; set; }
        public int? Limit { get; set; }

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
                var results = new List<FamilyTypeInfo>();

                var categoryIds = ResolveCategoryIds(CategoryList);
                bool filterByCategory = categoryIds.Count > 0;

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
                    results.Add(ToFamilyTypeInfo(symbol, symbol.FamilyName));
                    if (results.Count >= limit)
                    {
                        ResultFamilyTypes = results;
                        return;
                    }
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

                    results.Add(ToFamilyTypeInfo(systemType, familyName));
                    if (results.Count >= limit)
                        break;
                }

                ResultFamilyTypes = results;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", "获取族类型失败: " + ex.Message);
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
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
