import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import db from "../database/db.js";
import { listBriefRequirementsByType } from "../projectBrief/briefStore.js";
import {
  checkRoomAreaMin,
  checkRoomCount,
  groupRoomsByName,
  type ModelRoom,
} from "../projectBrief/checkAgainstBrief.js";

interface RawRoomsResponse {
  rooms?: Array<{ id?: number; name?: string; number?: string; area?: number }>;
}

export function registerCheckAgainstBriefTool(server: McpServer) {
  server.tool(
    "check_against_brief",
    "REV-182: compares the model's actual rooms against room_count/room_area_min requirements saved via " +
      "query_project_brief (run that with saveToLibrary=true first — this tool only reads what's already " +
      "saved, it does not re-parse a document). Matches by Revit Room name against the requirement's object " +
      "(e.g. a requirement for «студия» matches rooms named «Студия», «Студия 205», …). A requirement this " +
      "cannot find a matching room name for is reported with matched:false and an explanation, never a " +
      "silent 0 — this is expected for apartment-type requirements like «2-комнатная квартира», where the " +
      "unit is several Room elements, not one room carrying that exact name. Include unplaced/not-enclosed " +
      "rooms only if you want them counted as real.",
    {
      document: z
        .string()
        .optional()
        .describe("Restrict to requirements from this brief document (substring match). Omit to use all saved."),
      includeUnplacedRooms: z.boolean().optional().default(false),
      includeNotEnclosedRooms: z.boolean().optional().default(false),
    },
    async (args) => {
      try {
        const countRequirements = listBriefRequirementsByType(db, "room_count", args.document);
        const areaRequirements = listBriefRequirementsByType(db, "room_area_min", args.document);

        if (countRequirements.length === 0 && areaRequirements.length === 0) {
          return {
            content: [
              {
                type: "text" as const,
                text: JSON.stringify({
                  success: true,
                  countChecks: [],
                  areaChecks: [],
                  message:
                    "Нет сохранённых требований room_count/room_area_min для сверки. Сначала выполните " +
                    "query_project_brief с filePath и saveToLibrary:true.",
                }),
              },
            ],
          };
        }

        const roomsResponse = (await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("export_room_data", {
            includeUnplacedRooms: args.includeUnplacedRooms ?? false,
            includeNotEnclosedRooms: args.includeNotEnclosedRooms ?? false,
          });
        })) as RawRoomsResponse;

        const rooms: ModelRoom[] = (roomsResponse.rooms ?? [])
          .filter((r) => r.name)
          .map((r) => ({ id: r.id, name: r.name as string, number: r.number, area: r.area }));
        const groups = groupRoomsByName(rooms);

        const countChecks = countRequirements.map((req) =>
          checkRoomCount(req.object, Number(req.value), req.source, groups)
        );
        const areaChecks = areaRequirements.map((req) =>
          checkRoomAreaMin(req.object, Number(req.value), req.source, groups)
        );

        const discrepancies = [
          ...countChecks.filter((c) => c.matched && !c.ok).map((c) => c.message),
          ...areaChecks.filter((c) => c.matched && !c.ok).map((c) => c.message),
        ];
        const unmatched = [...countChecks, ...areaChecks].filter((c) => !c.matched).map((c) => c.object);

        return {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify({
                success: true,
                totalRoomsInModel: rooms.length,
                countChecks,
                areaChecks,
                discrepancyCount: discrepancies.length,
                message:
                  discrepancies.length === 0
                    ? unmatched.length === 0
                      ? "Все проверенные требования выполняются."
                      : `Расхождений нет среди сопоставленных требований; не сопоставлено с моделью: ${unmatched.join(", ")}.`
                    : discrepancies.join(" "),
              }),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `check_against_brief failed: ${error instanceof Error ? error.message : String(error)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
