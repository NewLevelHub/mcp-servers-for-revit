using System;
using System.Collections.Generic;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Live tool-step journal entry for the chat UI (REV-127).
    /// Raised before execute (<c>running</c>) and after parse (<c>ok</c>/<c>error</c>/<c>cancelled</c>).
    /// </summary>
    public sealed class ToolStepEvent
    {
        public const string StatusRunning = "running";
        public const string StatusOk = "ok";
        public const string StatusError = "error";
        public const string StatusCancelled = "cancelled";

        /// <summary>1-based index within the turn.</summary>
        public int Index { get; set; }

        public int Round { get; set; }

        public string Name { get; set; }

        /// <summary>running | ok | error | cancelled</summary>
        public string Status { get; set; }

        /// <summary>Short result summary from <c>ParseToolResponse</c>.</summary>
        public string Summary { get; set; }

        /// <summary>Truncated arguments JSON for expand panel.</summary>
        public string ArgsJson { get; set; }

        /// <summary>Truncated result JSON for expand panel.</summary>
        public string ResultJson { get; set; }

        public long DurationMs { get; set; }

        /// <summary>Full journal snapshot at raise time (immutable copy).</summary>
        public IReadOnlyList<ToolStepEvent> AllSteps { get; set; }

        public ToolStepEvent CloneWithoutAll()
        {
            return new ToolStepEvent
            {
                Index = Index,
                Round = Round,
                Name = Name,
                Status = Status,
                Summary = Summary,
                ArgsJson = ArgsJson,
                ResultJson = ResultJson,
                DurationMs = DurationMs,
            };
        }

        public static IReadOnlyList<ToolStepEvent> Snapshot(IList<ToolStepEvent> steps)
        {
            if (steps == null || steps.Count == 0)
                return Array.Empty<ToolStepEvent>();

            var copy = new ToolStepEvent[steps.Count];
            for (var i = 0; i < steps.Count; i++)
                copy[i] = steps[i].CloneWithoutAll();
            return copy;
        }
    }
}
