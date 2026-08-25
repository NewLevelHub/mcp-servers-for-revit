using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.Recorder;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.Recorder
{
    /// <summary>REV-177: replays a plugin-recorded action recipe on other levels — see ReplayRecordingEventHandler for the actual logic and its scope.</summary>
    public class ReplayRecordingCommand : ExternalEventCommandBase
    {
        private ReplayRecordingEventHandler _handler => (ReplayRecordingEventHandler)Handler;

        public override string CommandName => "replay_recording";

        public ReplayRecordingCommand(UIApplication uiApp)
            : base(new ReplayRecordingEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                var recordingId = parameters?["recordingId"]?.Value<string>()
                    ?? throw new ArgumentException("recordingId is required");

                List<string> targetLevelNames = null;
                if (parameters?["targetLevelNames"] is JArray namesArray)
                    targetLevelNames = namesArray.Select(t => t.Value<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

                int? fromFloor = parameters?["fromFloor"]?.Value<int?>();
                int? toFloor = parameters?["toFloor"]?.Value<int?>();
                bool confirm = parameters?["confirm"]?.Value<bool>() ?? false;

                _handler.SetParameters(recordingId, targetLevelNames, fromFloor, toFloor, confirm);

                if (RaiseAndWaitForCompletion(120000))
                {
                    return _handler.ResultInfo;
                }

                throw new TimeoutException("replay_recording operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to replay recording: {ex.Message}");
            }
        }
    }
}
