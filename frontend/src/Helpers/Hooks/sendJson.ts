import { ApiError } from './useApiQuery';

// A refusal of the API: its status and the code it gives ({ "error": "stepNotGranted" }), which the interface words.
export class ApiRefusal extends ApiError {
  constructor(
    status: number,
    public readonly code: string | null,
  ) {
    super(status, `${status} ${code ?? ''}`.trim());
  }
}

async function errorCode(response: Response): Promise<string | null> {
  try {
    const body = (await response.json()) as { error?: unknown };
    return typeof body.error === 'string' ? body.error : null;
  } catch {
    return null; // no body, or not JSON: the status is all there is
  }
}

// A write from a person carries this header, which a page of another site cannot add to a form or a link (the server refuses it without)
export const requestHeader = 'X-Orchestrarr-Request';

// The only way the interface changes something: components never call fetch. An answer with no body (204) gives back undefined.
export default async function sendJson<T>(method: 'POST' | 'PUT' | 'DELETE', path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    headers: { Accept: 'application/json', 'Content-Type': 'application/json', [requestHeader]: 'web' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    throw new ApiRefusal(response.status, await errorCode(response));
  }
  return (response.status === 204 ? undefined : await response.json()) as T;
}
