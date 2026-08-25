using System;
using System.Collections.Generic;

namespace revit_mcp_plugin.Core.Recorder
{
    /// <summary>
    /// A recorded action (REV-177): plain-old data, serialized to JSON and read back by
    /// BOTH the plugin (recording/list UI) and the commandset replay command — the two
    /// are separate assemblies with no project reference between them, so the JSON shape
    /// itself is the contract, not a shared compiled type. Field names here must match
    /// commandset/Models/Recorder/ReplayRecordingModels.cs exactly.
    /// </summary>
    public sealed class RecordedPoint
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public sealed class RecordedCurve
    {
        public RecordedPoint Start { get; set; }
        public RecordedPoint End { get; set; }
    }

    public sealed class RecordedAction
    {
        /// <summary>"create" | "modify" | "delete".</summary>
        public string Kind { get; set; } = "create";
        public long ElementId { get; set; }
        public string Category { get; set; }
        /// <summary>String form of the BuiltInCategory enum, e.g. "OST_Walls" — stable across UI language.</summary>
        public string BuiltInCategory { get; set; }
        public long? TypeId { get; set; }
        public string TypeName { get; set; }
        public long? LevelId { get; set; }
        public string LevelName { get; set; }
        /// <summary>Straight-line location (walls). Curved walls are recorded with this null — replay reports them unsupported rather than straightening them.</summary>
        public RecordedCurve Curve { get; set; }
        /// <summary>Point-based location (family instances).</summary>
        public RecordedPoint Point { get; set; }
        public double? Rotation { get; set; }
        /// <summary>ElementId of the host (e.g. the wall a door sits in), if any.</summary>
        public long? HostElementId { get; set; }
        /// <summary>Tracked parameter values at capture time — see ActionRecorder's TrackedParameterNames. Applied to the replayed element after creation.</summary>
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
        /// <summary>Set at capture time when this action's category/geometry is not one replay knows how to recreate — carried through so replay can report it without re-deriving.</summary>
        public string UnsupportedReason { get; set; }
    }

    public sealed class RecordedRecipe
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public DateTime RecordedUtc { get; set; }
        public long? SourceLevelId { get; set; }
        public string SourceLevelName { get; set; }
        /// <summary>Human sentence, e.g. «Поставил 4 перегородки 100 мм, 2 двери, 1 марку».</summary>
        public string SummaryText { get; set; }
        public List<RecordedAction> Actions { get; set; } = new List<RecordedAction>();
    }
}
