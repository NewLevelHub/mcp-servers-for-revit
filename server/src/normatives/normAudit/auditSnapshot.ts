import { withRevitConnection } from "../../utils/ConnectionManager.js";

/** Raw Revit payloads prefetched once for run_norm_audit (one mutex hold). */
export interface NormAuditRevitSnapshot {
  levelName: string;
  exportRoomData: unknown;
  exportTepData: unknown;
  doorEgressInfo: unknown;
  roomGeometryMetrics: unknown;
  openingGeometryInfo: unknown;
  verticalCirculationInfo: unknown;
}

/**
 * Fetch shared norm-audit geometry in a single withRevitConnection block.
 * Saves repeated mutex acquisitions; Revit work still runs sequentially inside Revit.
 */
export async function fetchNormAuditSnapshot(
  levelName: string
): Promise<NormAuditRevitSnapshot> {
  return withRevitConnection(async (client) => {
    const exportRoomData = await client.sendCommand("export_room_data", {
      includeUnplacedRooms: false,
      includeNotEnclosedRooms: false,
    });
    const exportTepData = await client.sendCommand("export_tep_data", {});
    const doorEgressInfo = await client.sendCommand("get_door_egress_info", {
      levelName,
    });
    const roomGeometryMetrics = await client.sendCommand(
      "get_room_geometry_metrics",
      {
        levelName,
        includeUnplacedRooms: false,
      }
    );
    const openingGeometryInfo = await client.sendCommand(
      "get_opening_geometry_info",
      { levelName }
    );
    const verticalCirculationInfo = await client.sendCommand(
      "get_vertical_circulation_info",
      { levelName }
    );

    return {
      levelName,
      exportRoomData,
      exportTepData,
      doorEgressInfo,
      roomGeometryMetrics,
      openingGeometryInfo,
      verticalCirculationInfo,
    };
  });
}
