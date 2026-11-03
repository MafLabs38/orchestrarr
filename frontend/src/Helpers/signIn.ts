// Signing in is the server's: the interface only sends the browser there when the API says nobody is signed in (a 401).
const lastAttemptKey = 'orchestrarr.signIn.lastAttempt';
// A sign-in that does not hold (a cookie that is refused) must not become a loop of redirects: no new attempt within this time of the last
const minimumGapMs = 10_000;

// What the host puts in the address when the sign-in failed (SignInPaths.Failed): the interface says so instead of trying again by itself
export const signInFailedMarker = 'signInFailed';

export const signInPath = '/auth/login';
export const signOutPath = '/auth/logout';

export function signInUrl(returnPath: string): string {
  return `${signInPath}?returnUrl=${encodeURIComponent(returnPath)}`;
}

export function signInFailed(search: string = globalThis.location.search): boolean {
  return new URLSearchParams(search).has(signInFailedMarker);
}

function attemptedRecently(): boolean {
  try {
    return Date.now() - Number(globalThis.sessionStorage.getItem(lastAttemptKey)) < minimumGapMs;
  } catch {
    // The storage is refused: there is no memory of the last attempt, and signing in is more useful than not trying
    return false;
  }
}

function markAttempt(): void {
  try {
    globalThis.sessionStorage.setItem(lastAttemptKey, String(Date.now()));
  } catch {
    // Nothing is remembered
  }
}

function clearAttempt(): void {
  try {
    globalThis.sessionStorage.removeItem(lastAttemptKey);
  } catch {
    // Nothing was remembered
  }
}

// Sends the browser to sign in, and brings it back to the page it was on. Does nothing if that was just tried, or if the last attempt failed.
export function redirectToSignIn(): void {
  const { pathname, search } = globalThis.location;
  if (signInFailed(search) || attemptedRecently()) {
    return;
  }
  markAttempt();
  globalThis.location.assign(signInUrl(pathname + search));
}

// What every answer of the API says about the session. A 401 sends the browser to sign in; an answer that came back means the session
// holds, so a later 401 (after a sign-out, an expired session) is a new sign-in to make and not a loop.
export function noteSession(response: Response): void {
  if (response.status === 401) {
    redirectToSignIn();
  } else if (response.ok) {
    clearAttempt();
  }
}
