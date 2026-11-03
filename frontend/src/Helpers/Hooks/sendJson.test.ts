import sendJson, { ApiRefusal } from './sendJson';

afterEach(() => {
  vi.unstubAllGlobals();
});

function stubFetch(response: Response) {
  const fetchMock = vi.fn(() => Promise.resolve(response));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

describe('sendJson', () => {
  it('sends the body as JSON with the method asked, and gives back the answer', async () => {
    const fetchMock = stubFetch(new Response(JSON.stringify({ ok: true }), { status: 201 }));

    const answer = await sendJson('POST', '/api/v1/approvals', { a: 1 });

    expect(answer).toEqual({ ok: true });
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/v1/approvals',
      expect.objectContaining({
        method: 'POST',
        body: '{"a":1}',
        headers: expect.objectContaining({ 'Content-Type': 'application/json' }),
      }),
    );
  });

  it('throws the status and the code the server gave', async () => {
    stubFetch(new Response(JSON.stringify({ error: 'stepNotGranted' }), { status: 422 }));

    const failure = await sendJson('POST', '/x', {}).catch((error: unknown) => error);

    expect(failure).toBeInstanceOf(ApiRefusal);
    expect(failure).toMatchObject({ status: 422, code: 'stepNotGranted' });
  });

  it('has no code when the answer is not the expected body', async () => {
    stubFetch(new Response('<html>Bad gateway</html>', { status: 502 }));

    const failure = await sendJson('PUT', '/x', {}).catch((error: unknown) => error);

    expect(failure).toMatchObject({ status: 502, code: null });
  });

  it('has no code when the error is not a string', async () => {
    stubFetch(new Response(JSON.stringify({ error: { nested: true } }), { status: 400 }));

    const failure = await sendJson('POST', '/x', {}).catch((error: unknown) => error);

    expect(failure).toMatchObject({ code: null });
  });
});
