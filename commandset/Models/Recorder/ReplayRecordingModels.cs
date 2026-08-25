using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Recorder
{
    /// <summary>
    /// Mirrors plugin/Core/Recorder/RecordedAction.cs field-for-field — the JSON on disk is the
    /// contract between the two assemblies (no project reference between plugin and commandset),
    /// so these class/property names must stay in lockstep with that file.
    /// </summary>
    public class RecordedPointModel
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public class RecordedCurveModel
    {
        public RecordedPointModel Start { get; set; }
        public RecordedPointModel End { get; set; }
    }

    public class RecordedActionModel
    {
        public string Kind { get; set; }
        public long ElementId { get; set; }
        public string Category { get; set; }
        public string BuiltInCategory { get; set; }
        public long? TypeId { get; set; }
        public string TypeName { get; set; }
        public long? LevelId { get; set; }
        public string LevelName { get; set; }
        public RecordedCurveModel Curve { get; set; }
        public RecordedPointModel Point { get; set; }
        public double? Rotation { get; set; }
        public long? HostElementId { get; set; }
        public Dictionary<string, string> Parameters { get; set; } = new();
        public string UnsupportedReason { get; set; }
    }

    public class RecordedRecipeModel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public DateTime RecordedUtc { get; set; }
        public long? SourceLevelId { get; set; }
        public string SourceLevelName { get; set; }
        public string SummaryText { get; set; }
        public List<RecordedActionModel> Actions { get; set; } = new();
    }

    public class ReplayActionResult
    {
        [JsonProperty("elementId")]
        public long ElementId { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("typeName")]
        public string TypeName { get; set; }

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("newElementId")]
        public long? NewElementId { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; }
    }

    public class ReplayLevelResult
    {
        [JsonProperty("levelName")]
        public string LevelName { get; set; }

        [JsonProperty("levelId")]
        public long LevelId { get; set; }

        [JsonProperty("createdCount")]
        public int CreatedCount { get; set; }

        [JsonProperty("failedCount")]
        public int FailedCount { get; set; }

        [JsonProperty("actions")]
        public List<ReplayActionResult> Actions { get; set; } = new();
    }

    public class ReplayRecordingResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("applied")]
        public bool Applied { get; set; }

        [JsonProperty("recordingId")]
        public string RecordingId { get; set; }

        [JsonProperty("recordingName")]
        public string RecordingName { get; set; }

        [JsonProperty("recipeSummary")]
        public string RecipeSummary { get; set; }

        [JsonProperty("levels")]
        public List<ReplayLevelResult> Levels { get; set; } = new();
    }
}
