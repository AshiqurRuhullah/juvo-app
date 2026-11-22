import { call } from "./cod";
import { sql } from "./env";
import { adminAccess, freshMobile } from "./returns";

/**
 * Helpers of the contact page and inbox specs. The API keeps counts per client address (10 messages an hour, counted from stored rows).
 * Every browser and every API call of the suite reaches it from the same local address, so after each send the stored address key is
 * replaced by a throw-away value (`freeAddress`). Production limits are not changed; only the test database is touched.
 */
export function freeAddress() {
  sql("UPDATE ContactMessages SET IpHash = CONVERT(varchar(36), Id) WHERE IpHash IS NOT NULL AND LEN(IpHash) <> 36");
}

export function uniqueToken(label: string): string {
  return `${label} ${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
}

export type SentMessage = { reference: string; mobile: string; token: string };

/** Sends a message through the public API (the way the page does) and frees the address budget again. */
export async function sendMessage(input: { name?: string; mobile?: string | null; email?: string | null; subject?: string; message?: string; token?: string } = {}): Promise<SentMessage> {
  const token = input.token ?? uniqueToken("Hello");
  const mobile = input.mobile === undefined ? freshMobile() : input.mobile;
  const answer = await call<{ reference: string }>("POST", "/api/v1/store/content/contact", {
    body: { name: input.name ?? "Nadia Rahman", mobile, email: input.email ?? null, subject: input.subject ?? token, message: input.message ?? `${token}: where is my parcel?`, sourcePage: "/contact", website: "" },
    expect: 202,
  });
  freeAddress();
  return { reference: answer.json.reference, mobile: mobile ?? "", token };
}

export type InboxItem = { id: string; reference: string; status: string; mobile: string | null; email: string | null; piiMasked: boolean };

export async function findMessages(search: string): Promise<InboxItem[]> {
  const token = await adminAccess();
  const list = await call<{ items: InboxItem[] }>("GET", `/api/v1/content/messages?search=${encodeURIComponent(search)}&pageSize=50`, { token, expect: 200 });
  return list.json.items;
}
