import sendJson from './Hooks/sendJson';
import { getJson } from './Hooks/useApiQuery';
import { redirectToSignIn, signInFailed } from './signIn';

function stubLocation(pathname: string, search = '') {
  const assign = vi.fn();
  vi.stubGlobal('location', { pathname, search, assign });
  return assign;
}

function stubFetch(status: number, body = '{}') {
  vi.stubGlobal(
    'fetch',
    vi.fn(() => Promise.resolve(new Response(body, { status }))),
  );
}

// The two ways the interface talks to the API: every answer goes through the same handling of the session
const requests = [
  ['a read', () => getJson('/api/v1/system/status')],
  ['a change', () => sendJson('PUT', '/api/v1/safety/kill-switch', {})],
] as const;

beforeEach(() => {
  globalThis.sessionStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('signIn', () => {
  it('sends the browser to sign in and brings it back to the page it was on', () => {
    const assign = stubLocation('/queue', '?severity=high');

    redirectToSignIn();

    expect(assign).toHaveBeenCalledWith('/auth/login?returnUrl=%2Fqueue%3Fseverity%3Dhigh');
  });

  it('does not try again right after an attempt: a sign-in that does not hold must not loop', () => {
    vi.useFakeTimers();
    const assign = stubLocation('/');

    redirectToSignIn();
    redirectToSignIn();
    vi.advanceTimersByTime(11_000);
    redirectToSignIn();

    expect(assign).toHaveBeenCalledTimes(2);
  });

  it('does not try again when the server sent the browser back because signing in failed', () => {
    const assign = stubLocation('/', '?signInFailed=1');

    redirectToSignIn();

    expect(assign).not.toHaveBeenCalled();
  });

  it('reads the marker of a failed sign-in in the address', () => {
    expect(signInFailed('?signInFailed=1')).toBe(true);
    expect(signInFailed('?other=1')).toBe(false);
  });

  it('still redirects when the browser refuses its storage: signing in is more useful than not trying', () => {
    const assign = stubLocation('/');
    vi.stubGlobal('sessionStorage', {
      getItem: () => {
        throw new Error('refused');
      },
      setItem: () => {
        throw new Error('refused');
      },
    });

    redirectToSignIn();

    expect(assign).toHaveBeenCalledTimes(1);
  });

  it.each(requests)('is what a 401 of %s does, and the request still fails', async (_name, request) => {
    const assign = stubLocation('/system/status');
    stubFetch(401, '{"error":"notSignedIn"}');

    await expect(request()).rejects.toMatchObject({ status: 401 });

    expect(assign).toHaveBeenCalledWith('/auth/login?returnUrl=%2Fsystem%2Fstatus');
  });

  it.each([403, 500])('is not what a %i does', async (code) => {
    const assign = stubLocation('/');
    stubFetch(code);

    for (const [, request] of requests) {
      await expect(request()).rejects.toMatchObject({ status: code });
    }

    expect(assign).not.toHaveBeenCalled();
  });

  it.each([403, 500])('does not clear the guard against a loop of redirects when it is a %i', async (code) => {
    const assign = stubLocation('/');
    redirectToSignIn();
    stubFetch(code);

    await expect(getJson('/api/v1/x')).rejects.toMatchObject({ status: code });
    redirectToSignIn();

    expect(assign).toHaveBeenCalledTimes(1);
  });

  it.each(requests)(
    'tries again after %s came back: the session held, so a later 401 is a new sign-in to make',
    async (_name, request) => {
      const assign = stubLocation('/');
      redirectToSignIn();
      stubFetch(200);

      await request();
      redirectToSignIn();

      expect(assign).toHaveBeenCalledTimes(2);
    },
  );
});
