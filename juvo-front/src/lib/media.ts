/** The API gives picture addresses under /files; this app serves them from /media (same origin, cached). Safe on server and client. */
export function mediaUrl(apiUrl: string | null | undefined): string | undefined {
  if (!apiUrl) return undefined;
  return apiUrl.startsWith("/files/") ? `/media/${apiUrl.slice("/files/".length)}` : apiUrl;
}
