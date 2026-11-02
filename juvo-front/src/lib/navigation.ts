/**
 * Full page navigation. Used after sign-in, sign-out and password changes on purpose: the server has just changed the session
 * cookies, and a full load guarantees the next server render sees them (a client-side transition can reuse a cached page).
 */
export function hardNavigate(url: string) {
  window.location.assign(url);
}
