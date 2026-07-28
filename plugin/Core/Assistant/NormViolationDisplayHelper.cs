using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Reliable highlight pipeline for norm audit: only violation rooms (filled regions),
    /// violation doors (SetColor), optional leader annotations.
    /// </summary>
    public static class NormViolationDisplayHelper
    {
        public static JObject Highlight(JArray findings, bool annotate, bool clearPrevious)
        {
            SplitFindings(findings, out var roomIds, out var doorIds, violationOnly: true);

            var steps = new JArray();
            var errors = new List<string>();

            if (roomIds.Count == 0 && doorIds.Count == 0)
            {
                return new JObject
                {
                    ["success"] = true,
                    ["Success"] = true,
                    ["message"] = "Нарушений для подсветки не найдено (только status=violation).",
                    ["roomCount"] = 0,
                    ["doorCount"] = 0,
                    ["steps"] = steps
                };
            }

            if (roomIds.Count > 0)
            {
                var frPayload = new JObject
                {
                    ["roomIds"] = new JArray(roomIds),
                    ["colorPreset"] = "red",
                    ["clearPrevious"] = clearPrevious
                };
                var frRaw = SocketService.Instance.ExecuteJsonRpcLocal(
                    "create_filled_regions",
                    frPayload.ToString(Formatting.None));
                var frOk = TryParseSuccess(frRaw, out var frMsg, out var frCount);
                steps.Add(new JObject
                {
                    ["step"] = "create_filled_regions",
                    ["ok"] = frOk,
                    ["count"] = frCount,
                    ["message"] = frMsg
                });
                if (!frOk)
                    errors.Add(frMsg);
                clearPrevious = false;
            }

            if (doorIds.Count > 0)
            {
                var opPayload = new JObject
                {
                    ["data"] = new JObject
                    {
                        ["action"] = "SetColor",
                        ["elementIds"] = new JArray(doorIds),
                        ["colorValue"] = new JArray(255, 0, 0)
                    }
                };
                var opRaw = SocketService.Instance.ExecuteJsonRpcLocal(
                    "operate_element",
                    opPayload.ToString(Formatting.None));
                var opOk = TryParseSuccess(opRaw, out var opMsg, out _);
                steps.Add(new JObject
                {
                    ["step"] = "operate_element",
                    ["ok"] = opOk,
                    ["count"] = doorIds.Count,
                    ["message"] = opMsg
                });
                if (!opOk)
                    errors.Add(opMsg);
            }

            if (annotate && findings.Count > 0)
            {
                var forAnnotate = new JArray();
                foreach (JToken f in findings)
                {
                    var status = f["status"]?.ToString() ?? "";
                    if (status.Equals("violation", StringComparison.OrdinalIgnoreCase))
                        forAnnotate.Add(f);
                }

                if (forAnnotate.Count > 0)
                {
                    var annPayload = new JObject
                    {
                        ["findings"] = forAnnotate,
                        ["style"] = "leader",
                        ["clearPrevious"] = clearPrevious
                    };
                    var annRaw = AnnotateNormFindingsHelper.Run(annPayload.ToString(Formatting.None));
                    var annOk = TryParseSuccess(annRaw, out var annMsg, out var annCount);
                    steps.Add(new JObject
                    {
                        ["step"] = "annotate_norm_findings",
                        ["ok"] = annOk,
                        ["count"] = annCount,
                        ["message"] = annMsg
                    });
                    if (!annOk)
                        errors.Add(annMsg);
                }
            }

            return new JObject
            {
                ["success"] = errors.Count == 0,
                ["Success"] = errors.Count == 0,
                ["message"] = errors.Count == 0
                    ? $"Подсвечено: помещений {roomIds.Count}, дверей {doorIds.Count}."
                    : string.Join("; ", errors),
                ["roomCount"] = roomIds.Count,
                ["doorCount"] = doorIds.Count,
                ["steps"] = steps
            };
        }

        public static void SplitFindings(
            JArray findings,
            out List<long> roomIds,
            out List<int> doorIds,
            bool violationOnly)
        {
            roomIds = new List<long>();
            doorIds = new List<int>();
            if (findings == null)
                return;

            var roomSeen = new HashSet<long>();
            var doorSeen = new HashSet<int>();

            foreach (JToken f in findings)
            {
                var status = f["status"]?.ToString() ?? "";
                if (violationOnly
                    && !status.Equals("violation", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!violationOnly
                    && !status.Equals("violation", StringComparison.OrdinalIgnoreCase)
                    && !status.Equals("nearLimit", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = f["elementId"]?.Value<long?>()
                    ?? f["ElementId"]?.Value<long?>()
                    ?? f["id"]?.Value<long?>()
                    ?? 0;
                if (id <= 0)
                    continue;

                if (IsDoorFinding(f))
                {
                    var doorId = (int)id;
                    if (doorSeen.Add(doorId))
                        doorIds.Add(doorId);
                }
                else
                {
                    if (roomSeen.Add(id))
                        roomIds.Add(id);
                }
            }
        }

        public static bool IsDoorFinding(JToken f)
        {
            var check = (f["checkType"]?.ToString() ?? "").ToLowerInvariant();
            return check.Contains("fire_door")
                || check.Contains("door_clear")
                || check.Contains("door_width")
                || check.Contains("check_door");
        }

        private static bool TryParseSuccess(string raw, out string message, out int count)
        {
            message = "";
            count = 0;
            try
            {
                var jo = JObject.Parse(raw);
                if (jo["error"] != null)
                {
                    message = jo["error"]?["message"]?.ToString() ?? "ошибка";
                    return false;
                }

                var result = jo["result"] as JObject ?? jo;
                var success = result["Success"]?.Value<bool?>()
                    ?? result["success"]?.Value<bool?>()
                    ?? true;
                message = result["Message"]?.ToString()
                    ?? result["message"]?.ToString()
                    ?? "";
                count = result["createdCount"]?.Value<int?>()
                    ?? result["CreatedCount"]?.Value<int?>()
                    ?? 0;
                if (count == 0 && result["notes"] is JArray notesArr)
                    count = notesArr.Count;
                return success;
            }
            catch
            {
                message = raw ?? "";
                return false;
            }
        }
    }
}
