namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Finding the solid fill pattern, once, for everything that paints.
    ///
    /// A project holds more than one solid fill: the standard drafting «Solid fill»
    /// and model copies of it that arrive with families and templates. A plain
    /// FilteredElementCollector returns them in no particular order, so four call
    /// sites doing their own <c>FirstOrDefault(p =&gt; p.IsSolidFill)</c> could each
    /// land on a different one, in a different project, on a different day.
    ///
    /// It matters most for ColorFillSchemeEntry, which wants the drafting pattern:
    /// hand it a model one and Revit accepts the entry, then fails the fill
    /// calculation («Не удалось выполнить расчёт Цветовая заливка») and paints a
    /// fallback hatch over the rooms instead of the colours (19.08.2026).
    /// </summary>
    public static class SolidFillPatterns
    {
        /// <summary>
        /// The drafting solid fill, or any solid fill when the project has none.
        /// <see cref="ElementId.InvalidElementId"/> when the project has no solid
        /// fill at all — callers must check, painting without one is a no-op.
        /// </summary>
        public static ElementId FindId(Document doc)
        {
            return Find(doc)?.Id ?? ElementId.InvalidElementId;
        }

        /// <summary>The pattern element behind <see cref="FindId"/>.</summary>
        public static FillPatternElement Find(Document doc)
        {
            if (doc == null)
            {
                return null;
            }

            var solids = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .Select(element => new { element, pattern = element.GetFillPattern() })
                .Where(entry => entry.pattern != null && entry.pattern.IsSolidFill)
                .ToList();

            var drafting = solids
                .FirstOrDefault(entry => entry.pattern.Target == FillPatternTarget.Drafting);

            return drafting?.element ?? solids.FirstOrDefault()?.element;
        }
    }
}
