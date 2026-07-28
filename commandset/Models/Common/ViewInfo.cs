namespace RevitMCPCommandSet.Models.Common
{
    public class CurrentViewInfo
    {
        public long Id { get; set; }
        public string UniqueId { get; set; }
        public string Name { get; set; }
        public string ViewType { get; set; }
        public bool IsTemplate { get; set; }
        public int Scale { get; set; }
        public string DetailLevel { get; set; }
        /// <summary>GenLevel name for floor plans (use for norm checks, not View.Name).</summary>
        public string LevelName { get; set; }
        public long? LevelId { get; set; }
        public double? LevelElevationMm { get; set; }
    }
}
