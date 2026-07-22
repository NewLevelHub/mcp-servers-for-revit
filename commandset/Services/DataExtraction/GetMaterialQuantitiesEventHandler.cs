using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    public class GetMaterialQuantitiesEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private List<string> _categoryFilters;
        private bool _selectedElementsOnly;

        public GetMaterialQuantitiesResult ResultInfo { get; private set; }
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetParameters(List<string> categoryFilters = null, bool selectedElementsOnly = false)
        {
            _categoryFilters = categoryFilters;
            _selectedElementsOnly = selectedElementsOnly;
            TaskCompleted = false;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            // Do not Reset here - SetParameters/Prepare already Reset before Raise.
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var uiDoc = app.ActiveUIDocument;
                var doc = uiDoc.Document;
                var materialData = new Dictionary<ElementId, MaterialQuantityModel>();

                if (_selectedElementsOnly)
                {
                    foreach (var id in uiDoc.Selection.GetElementIds())
                    {
                        var element = doc.GetElement(id);
                        if (element != null)
                            AccumulateMaterialQuantities(doc, element, materialData);
                    }
                }
                else
                {
                    var collector = new FilteredElementCollector(doc)
                        .WhereElementIsNotElementType();

                    if (_categoryFilters != null && _categoryFilters.Count > 0)
                    {
                        var builtInCategories = new List<BuiltInCategory>();
                        foreach (var catName in _categoryFilters)
                        {
                            if (Enum.TryParse(catName, out BuiltInCategory cat))
                                builtInCategories.Add(cat);
                        }

                        if (builtInCategories.Count > 0)
                            collector = collector.WherePasses(new ElementMulticategoryFilter(builtInCategories));
                    }

                    // Iterate without ToElements() to avoid materializing the full list.
                    foreach (Element element in collector)
                        AccumulateMaterialQuantities(doc, element, materialData);
                }

                ResultInfo = BuildResult(materialData);
            }
            catch (Exception ex)
            {
                ResultInfo = new GetMaterialQuantitiesResult
                {
                    Success = false,
                    Message = $"Error calculating material quantities: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        private static void AccumulateMaterialQuantities(
            Document doc,
            Element element,
            Dictionary<ElementId, MaterialQuantityModel> materialData)
        {
            ICollection<ElementId> materialIds;
            try
            {
                materialIds = element.GetMaterialIds(false);
            }
            catch
            {
                return;
            }

            foreach (ElementId matId in materialIds)
            {
                if (doc.GetElement(matId) is not Material material)
                    continue;

                if (!materialData.TryGetValue(matId, out var quantity))
                {
                    quantity = new MaterialQuantityModel
                    {
#if REVIT2024_OR_GREATER
                        MaterialId = matId.Value,
#else
                        MaterialId = matId.IntegerValue,
#endif
                        MaterialName = material.Name,
                        MaterialClass = material.MaterialClass
                    };
                    materialData[matId] = quantity;
                }

                quantity.Area += element.GetMaterialArea(matId, false);
                quantity.Volume += element.GetMaterialVolume(matId);

                long elementIdValue = element.Id.GetValue();
                if (!quantity.ElementIds.Contains(elementIdValue))
                {
                    quantity.ElementIds.Add(elementIdValue);
                    quantity.ElementCount++;
                }
            }
        }

        private static GetMaterialQuantitiesResult BuildResult(
            Dictionary<ElementId, MaterialQuantityModel> materialData)
        {
            var materials = materialData.Values.ToList();
            return new GetMaterialQuantitiesResult
            {
                TotalMaterials = materials.Count,
                TotalArea = materials.Sum(m => m.Area),
                TotalVolume = materials.Sum(m => m.Volume),
                Materials = materials,
                Success = true,
                Message = $"Successfully calculated quantities for {materials.Count} materials"
            };
        }

        public string GetName()
        {
            return "Get Material Quantities";
        }
    }
}
