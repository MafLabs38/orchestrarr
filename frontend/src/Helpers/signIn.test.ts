import sendJson from './Hooks/sendJson';
import { getJson } from './Hooks/useApiQuery';
import { redirectToSignIn, signInFailed, signInUrl } from './signIn';

function stubLocation(pathname: string, search = '') {
  const assign = vi.fn();
  vi.stubGlobal('location', { pathname, search, assign });
  return assign;
}

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

  it('builds the address of the sign-in from a path', () => {
    expect(signInUrl('/')).toBe('/auth/login?returnUrl=%2F');
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

  it('tries again after an answer came back: the session held, so a later 401 is a new sign-in to make', async () => {
    const assign = stubLocation('/');
    redirectToSignIn();
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response('{}', { status: 200 }))),
    );

    await getJson('/api/v1/system/status');
    await sendJson('PUT', '/api/v1/safety/kill-switch', {});
    redirectToSignIn();

    expect(assign).toHaveBeenCalledTimes(2);
  });

  it('does not try again when the server sent the browser back because signing in failed', () => {
    const assign = stubLocation('/', '?signInFailed=1');

    redirectToSignIn();

    expect(assign).not.toHaveBeenCalled();
    expect(signInFailed('?signInFailed=1')).toBe(true);
    expect(signInFailed('?other=1')).toBe(false);
  });

  it('still redirects when the browser refuses its storage', () => {
    const assign = stubLocation('/');
    vi.stubGlobal('sessionStorage', {
      getItem: () => {
        throw new Error('refused');
      },
      setItem: () => undefined,
    });

    redirectToSignIn();

    expect(assign).toHaveBeenCalledTimes(1);
  });

  it('is what a 401 of a read does, and the read still fails', async () => {
    const assign = stubLocation('/system/status');
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response('', { status: 401 }))),
    );

    await expect(getJson('/api/v1/work-items')).rejects.toMatchObject({ status: 401 });

    expect(assign).toHaveBeenCalledWith('/auth/login?returnUrl=%2Fsystem%2Fstatus');
  });

  it('is what a 401 of a change does, and the change still fails', async () => {
    const assign = stubLocation('/');
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response('{"error":"notSignedIn"}', { status: 401 }))),
    );

    await expect(sendJson('POST', '/api/v1/approvals', {})).rejects.toMatchObject({ status: 401, code: 'notSignedIn' });

    expect(assign).toHaveBeenCalledTimes(1);
  });

  it('is not what any other refusal does', async () => {
    const assign = stubLocation('/');
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response('', { status: 403 }))),
    );

    await expect(getJson('/api/v1/x')).rejects.toMatchObject({ status: 403 });

    expect(assign).not.toHaveBeenCalled();
  });
});
