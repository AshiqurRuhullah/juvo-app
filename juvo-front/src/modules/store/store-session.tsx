"use client";

import * as React from "react";

type StoreSession = {
  /** A customer (not staff) is signed in: wishlist and account features work. */
  customer: boolean;
  /** Where the sign-in link should come back to is decided where it is shown; this is just the sign-in address. */
  signInHref: string;
};

const Context = React.createContext<StoreSession>({ customer: false, signInHref: "/login?as=customer" });

/** Tells the shop's client components whether a customer is signed in (decided on the server by the layout from the session cookie). */
export function StoreSessionProvider({ customer, children }: { customer: boolean; children: React.ReactNode }) {
  const value = React.useMemo(() => ({ customer, signInHref: "/login?as=customer" }), [customer]);
  return <Context.Provider value={value}>{children}</Context.Provider>;
}

export const useStoreSession = () => React.useContext(Context);

/** `/login?as=customer&next=<where you are>`: sign in and come straight back. */
export function signInUrl(next: string): string {
  return `/login?as=customer&next=${encodeURIComponent(next)}`;
}
