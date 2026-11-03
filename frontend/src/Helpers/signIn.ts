// Signing in is the server's: the interface only sends the browser there when the API says nobody is signed in (a 401).
const lastAttemptKey = 'orchestrarr.signIn.lastAttempt';
// A sign-in that does not hold (a cookie that is refused) must not become a loop of redirects
const minimumGapMs = 10_000;

// What the server puts in the address when the sign-in failed: the interface says so instead of trying again by itself
export const signInFailedMarker = 'signInFailed';

export const signInPath = '/auth/login';
export const signOutPath = '/auth/logout';

export function signInUrl(returnPath: string): string {
  return `${signInPath}?returnUrl=${encodeURIComponent(returnPath)}`;
}

export function signInFailed(search: string = globalThis.location.search): boolean {
  return new URLSearchParams(search).has(signInFailedMarker);
}

function triedJustNow(): boolean {
  try {
    const last = Number(globalThis.sessionStorage.getItem(lastAttemptKey));
    if (Date.now() - last < minimumGapMs) {
      return true;
    }
    globalThis.sessionStorage.setItem(lastAttemptKey, String(Date.now()));
  } catch {
    // The storage is refused: there is no memory of the last attempt, so the redirect goes ahead
  }
  return false;
}

// A request that was answered means the session holds: the next 401 (after a sign-out, an expired session) may send the browser to sign in again
export function signInHolds(): void {
  try {
    globalThis.sessionStorage.removeItem(lastAttemptKey);
  } catch {
    // The storage is refused: nothing was remembered
  }
}

// Sends the browser to sign in, and brings it back to the page it was on. Does nothing if that was just tried, or if the last attempt failed.
export function redirectToSignIn(): void {
  const { pathname, search } = globalThis.location;
  if (signInFailed(search) || triedJustNow()) {
    return;
  }
  globalThis.location.assign(signInUrl(pathname + search));
}
