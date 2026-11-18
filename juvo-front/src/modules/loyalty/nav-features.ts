"use client";

import * as React from "react";
import { useMyRewards } from "./api";

/**
 * Which switchable features are on, for the menu. Only the customer area asks (one small call, cached for a minute): the rewards
 * entry exists only while the API says the rewards feature is on (it answers 409 LOYALTY.FEATURE_OFF otherwise). Staff always see their
 * `Loyalty` screens, so the shop can set the rules up first.
 */
export function useNavFeatures(area: "admin" | "account"): { loyalty: boolean } {
  const rewards = useMyRewards(area === "account");
  const on = area === "account" && !!rewards.data;
  return React.useMemo(() => ({ loyalty: on }), [on]);
}
