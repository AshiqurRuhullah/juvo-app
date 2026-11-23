import { describe, expect, it } from "vitest";
import {
  IDLE_REFRESH_MS, OLD_AFTER_HOURS, RUNNING_REFRESH_MS, ageHours, anyRunning, backupState, backupStateLook, formatAge, formatBytes, formatMegabytes, formatRunDuration, isGoodRun,
  isKnownInventoryKey, orderInventory, pollInterval, startFailure,
} from "@/modules/system/backup-logic";

const now = new Date("2026-11-23T12:00:00Z");
const units = { d: "d", h: "h", min: "min", s: "s" };

describe("state of the newest verified backup", () => {
  it("is Never without a verified backup, enabled or not", () => {
    expect(backupState({ enabled: true, lastVerifiedAt: null, lastVerifiedAgeHours: null }, now)).toBe("never");
    expect(backupState({ enabled: false, lastVerifiedAt: null }, now)).toBe("never");
  });
  it("is OK up to 26 hours and Old beyond when automatic backups are on", () => {
    expect(OLD_AFTER_HOURS).toBe(26);
    expect(backupState({ enabled: true, lastVerifiedAt: "2026-11-23T02:30:00Z", lastVerifiedAgeHours: 9.5 }, now)).toBe("ok");
    expect(backupState({ enabled: true, lastVerifiedAt: "x", lastVerifiedAgeHours: 26 }, now)).toBe("ok");
    expect(backupState({ enabled: true, lastVerifiedAt: "x", lastVerifiedAgeHours: 26.01 }, now)).toBe("old");
    expect(backupState({ enabled: true, lastVerifiedAt: "x", lastVerifiedAgeHours: "30.5" }, now)).toBe("old");
  });
  it("is never green or Old when automatic backups are off (neutral manual state)", () => {
    expect(backupState({ enabled: false, lastVerifiedAt: "x", lastVerifiedAgeHours: 2 }, now)).toBe("manual");
    expect(backupState({ enabled: false, lastVerifiedAt: "x", lastVerifiedAgeHours: 500 }, now)).toBe("manual");
  });
  it("computes the age from the time when the API sends none, and never negative", () => {
    expect(ageHours({ lastVerifiedAt: "2026-11-23T06:00:00Z" }, now)).toBeCloseTo(6);
    expect(ageHours({ lastVerifiedAt: "2026-11-24T06:00:00Z" }, now)).toBe(0);
    expect(ageHours({ lastVerifiedAt: "garbage" }, now)).toBeNull();
    expect(ageHours({ lastVerifiedAt: null, lastVerifiedAgeHours: 3 }, now)).toBeNull();
  });
  it("gives each state its own colour family and icon", () => {
    const icons = (["ok", "old", "never", "manual"] as const).map((s) => backupStateLook(s).icon);
    expect(new Set(icons).size).toBe(4);
    expect(backupStateLook("ok").tone).toBe("success");
    expect(backupStateLook("old").tone).toBe("danger");
    expect(backupStateLook("never").tone).toBe("danger");
  });
});

describe("formatting", () => {
  it("ages in two units", () => {
    expect(formatAge(0.02, units)).toBe("1 min");
    expect(formatAge(9.5, units)).toBe("9 h 30 min");
    expect(formatAge(51, units)).toBe("2 d 3 h");
    expect(formatAge(0, units)).toBe("0 s");
  });
  it("sizes", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(1023)).toBe("1023 B");
    expect(formatBytes(1536)).toBe("1.5 KB");
    expect(formatBytes(8_413_696)).toBe("8.0 MB");
    expect(formatBytes("3221225472")).toBe("3.0 GB");
    expect(formatBytes(null)).toBe("-");
    expect(formatBytes(-1)).toBe("-");
  });
  it("uploads always in megabytes", () => {
    expect(formatMegabytes(0)).toBe("0.0 MB");
    expect(formatMegabytes(12_900_000)).toBe("12.3 MB");
    expect(formatMegabytes(null)).toBe("-");
  });
  it("run durations", () => {
    expect(formatRunDuration(45, units)).toBe("45 s");
    expect(formatRunDuration(125, units)).toBe("2 min 5 s");
    expect(formatRunDuration(null, units)).toBe("-");
  });
});

describe("runs and polling", () => {
  it("a good run is Succeeded and verified", () => {
    expect(isGoodRun({ status: "Succeeded", verified: true })).toBe(true);
    expect(isGoodRun({ status: "Failed", verified: false })).toBe(false);
    expect(isGoodRun({ status: "Succeeded", verified: false })).toBe(false);
  });
  it("sees a running backup from the status or from the list", () => {
    expect(anyRunning({ running: true }, [])).toBe(true);
    expect(anyRunning({ running: false }, [{ status: "Running" }])).toBe(true);
    expect(anyRunning({ running: false }, [{ status: "Succeeded" }])).toBe(false);
    expect(anyRunning(undefined, undefined)).toBe(false);
  });
  it("polls every 15 s while running, every 60 s otherwise and not at all in a hidden tab", () => {
    expect(pollInterval(true, true)).toBe(RUNNING_REFRESH_MS);
    expect(RUNNING_REFRESH_MS).toBe(15_000);
    expect(pollInterval(false, true)).toBe(IDLE_REFRESH_MS);
    expect(IDLE_REFRESH_MS).toBe(60_000);
    expect(pollInterval(true, false)).toBe(false);
    expect(pollInterval(false, false)).toBe(false);
  });
});

describe("start failure mapping", () => {
  it("409 and its code mean a backup is already running", () => {
    expect(startFailure({ status: 409, code: "SYSTEM.BACKUP_RUNNING" })).toBe("running");
    expect(startFailure({ status: 409 })).toBe("running");
    expect(startFailure({ code: "SYSTEM.BACKUP_RUNNING" })).toBe("running");
  });
  it("403 is forbidden, anything else is an error to show", () => {
    expect(startFailure({ status: 403 })).toBe("forbidden");
    expect(startFailure({ status: 500, code: "GENERAL.UNEXPECTED" })).toBe("other");
    expect(startFailure(undefined)).toBe("other");
  });
});

describe("data inventory order", () => {
  it("shows the known keys in a fixed order and keeps unknown keys last", () => {
    const rows = [{ key: "auditLogs" }, { key: "zebra" }, { key: "customers" }, { key: "orders" }];
    expect(orderInventory(rows).map((r) => r.key)).toEqual(["customers", "orders", "auditLogs", "zebra"]);
    expect(isKnownInventoryKey("variants")).toBe(true);
    expect(isKnownInventoryKey("zebra")).toBe(false);
  });
});
