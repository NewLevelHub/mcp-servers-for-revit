import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  COMMAND_TIMEOUT_MS,
  computeReconnectDelayMs,
  formatRevitError,
  RECONNECT_BASE_MS,
  RECONNECT_MAX_MS,
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
