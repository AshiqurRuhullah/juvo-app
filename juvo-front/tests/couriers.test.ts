import { describe, expect, it } from "vitest";
import {
  estimateCourierCost,
  formatWeight,
  isOpenShipment,
  isValidReceived,
  journeyIndex,
  safeTrackingUrl,
  settlementTone,
  shipmentTone,
  varianceOf,
} from "@/modules/couriers/status";

describe("parcel status helpers", () => {
  it("knows which parcels are still open", () => {
    expect(isOpenShipment("Booked")).toBe(true);
    expect(isOpenShipment("DeliveryFailed")).toBe(true);
    for (const done of ["Delivered", "Returned", "Cancelled"]) expect(isOpenShipment(done)).toBe(false);
  });

  it("places a failed delivery on the out-for-delivery step and unknown statuses off the path", () => {
    expect(journeyIndex("Booked")).toBe(0);
    expect(journeyIndex("Delivered")).toBe(4);
    expect(journeyIndex("DeliveryFailed")).toBe(journeyIndex("OutForDelivery"));
    expect(journeyIndex("Cancelled")).toBe(-1);
  });

  it("gives failure a danger tone and finished-with-no-news a neutral one", () => {
    expect(shipmentTone("DeliveryFailed")).toBe("danger");
    expect(shipmentTone("Delivered")).toBe("success");
    expect(shipmentTone("Cancelled")).toBe("neutral");
    expect(settlementTone("Settled")).toBe("success");
  });
});

describe("courier cost estimate", () => {
  it("charges the base rate up to the base weight and per started kilogram above it", () => {
    expect(estimateCourierCost(60, 20, 1000, 600)).toBe(60);
    expect(estimateCourierCost(60, 20, 1000, 1000)).toBe(60);
    expect(estimateCourierCost(60, 20, 1000, 1001)).toBe(80);
    expect(estimateCourierCost(60, 20, 1000, 2500)).toBe(100);
  });

  it("formats weights", () => {
    expect(formatWeight(600)).toBe("600 g");
    expect(formatWeight(1250)).toBe("1.25 kg");
    expect(formatWeight(null)).toBe("-");
  });
});

describe("COD reconciliation helpers", () => {
  it("tells short, over and exact apart", () => {
    expect(varianceOf(1560, 1510)).toBe("short");
    expect(varianceOf(2060, 2080)).toBe("over");
    expect(varianceOf(1060, 1060)).toBe("exact");
    expect(varianceOf(0.3, 0.1 + 0.2)).toBe("exact");
  });

  it("accepts only non-negative amounts with at most two decimals", () => {
    for (const ok of ["0", "10", "1510.5", "1510.50", " 7 "]) expect(isValidReceived(ok), ok).toBe(true);
    for (const bad of ["", "-1", "1.234", "abc", "1,5", "1e3"]) expect(isValidReceived(bad), bad).toBe(false);
  });
});

describe("tracking links", () => {
  it("shows https links only", () => {
    expect(safeTrackingUrl("https://track.example/SBX-1")).toBe("https://track.example/SBX-1");
    expect(safeTrackingUrl("http://track.example/SBX-1")).toBeNull();
    expect(safeTrackingUrl("javascript:alert(1)")).toBeNull();
    expect(safeTrackingUrl(null)).toBeNull();
  });
});
