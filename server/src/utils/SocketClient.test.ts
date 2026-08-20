import assert from "node:assert/strict";
import * as net from "node:net";
import { describe, it } from "node:test";
import {
  COMMAND_TIMEOUT_MS,
  computeReconnectDelayMs,
  formatRevitError,
  RECONNECT_BASE_MS,
  RECONNECT_MAX_MS,
  RevitClientConnection,
  RevitConnectionError,
} from "./SocketClient.js";

describe("computeReconnectDelayMs", () => {
  it("starts at base delay and doubles each attempt", () => {
    assert.equal(computeReconnectDelayMs(0), RECONNECT_BASE_MS);
    assert.equal(computeReconnectDelayMs(1), RECONNECT_BASE_MS * 2);
    assert.equal(computeReconnectDelayMs(2), RECONNECT_BASE_MS * 4);
  });

  it("caps at RECONNECT_MAX_MS (15s AC)", () => {
    assert.equal(computeReconnectDelayMs(10), RECONNECT_MAX_MS);
    assert.ok(computeReconnectDelayMs(5) <= RECONNECT_MAX_MS);
  });
});

describe("formatRevitError", () => {
  it("distinguishes server offline vs unresponsive vs command timeout", () => {
    assert.match(formatRevitError("server_offline"), /Open Server/);
    assert.match(formatRevitError("revit_unresponsive"), /переподключен/i);
    assert.match(
      formatRevitError("command_timeout", `Команда превысила ${COMMAND_TIMEOUT_MS / 1000} с: foo`),
      /превысила/
    );
  });
});

describe("RevitConnectionError", () => {
  it("carries a typed kind for callers", () => {
    const err = new RevitConnectionError(
      "command_timeout",
      formatRevitError("command_timeout", "Команда превысила 120 с: say_hello")
    );
    assert.equal(err.kind, "command_timeout");
    assert.equal(err.name, "RevitConnectionError");
    // Timeout messaging must not imply the channel was closed.
    assert.doesNotMatch(err.message, /потеряна|выключен|закрыто/i);
  });
});

/**
 * The connection is kept open for the whole process lifetime on purpose, and
 * with Revit running it never closes on its own. That is fine for the MCP
 * server, which its stdio transport holds open anyway — and fatal for anything
 * else: `node --test` finished all 481 assertions and then sat there until it
 * was killed, because one connected socket was still holding the event loop.
 * The heartbeat and reconnect timers had been unref'd for exactly this reason;
 * the socket had not.
 *
 * Asserted through the libuv handle because `net.Socket#hasRef` does not exist
 * in this Node build.
 */
describe("socket ref-counting", () => {
  const handleHasRef = (connection: RevitClientConnection): boolean | undefined =>
    (connection.socket as unknown as { _handle?: { hasRef?: () => boolean } })
      ._handle?.hasRef?.();

  it("does not hold the event loop while idle", async () => {
    const server = net.createServer(() => {});
    await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
    const { port } = server.address() as net.AddressInfo;

    const connection = new RevitClientConnection("127.0.0.1", port);
    try {
      await connection.connect();
      assert.equal(
        handleHasRef(connection),
        false,
        "an idle connection must not keep the process alive"
      );
    } finally {
      connection.disconnect();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  });
});
