import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerExportApartmentDataTool(server: McpServer) {
  server.tool(
    "export_apartment_data",
    "Квартирография и ТЭП жилого дома. Groups placed rooms into apartments by the apartment-number room parameter (auto-discovers ADSK_Номер квартиры / Номер квартиры / Квартира etc., or pass apartmentNumberParameter). Computes per-apartment areas in m² per СП РК 3.02-101-2012* (Приложение А, п. А.8): жилая (living rooms), полезная (living + auxiliary, no summer rooms), приведённая летних (loggia ×0.5, balcony/terrace ×0.3, veranda ×0.8, combined ×0.4) and общая (полезная + приведённая). Returns ведомость квартир (number, type Студия/1К/2К/3К…, level, areas) and сводный ТЭП by type (count, share %, total/avg areas), plus the norm quote for the coefficients. Rooms without an apartment number (МОП) are reported separately.",
    {
      apartmentNumberParameter: z
        .string()
        .optional()
        .default("")
        .describe(
          "Room parameter holding the apartment number, e.g. 'ADSK_Номер квартиры'. Auto-discovered from common RU names when empty."
        ),
      includeRooms: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "Include the per-room breakdown (name, category, coefficient, counted area) inside each apartment. Off by default: on large models it inflates the response."
        ),
    },
    async (args) => {
      const params = {
        apartmentNumberParameter: args.apartmentNumberParameter ?? "",
        includeRooms: args.includeRooms ?? false,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("export_apartment_data", params);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(response),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Export apartment data failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
