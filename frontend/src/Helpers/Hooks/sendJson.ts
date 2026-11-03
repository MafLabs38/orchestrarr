import { redirectToSignIn, signInHolds } from 'Helpers/signIn';

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

// The only way the interface changes something: components never call fetch.
export default async function sendJson<T>(method: 'POST' | 'PUT', path: string, body: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    if (response.status === 401) {
      redirectToSignIn();
    }
    throw new ApiRefusal(response.status, await errorCode(response));
  }
  signInHolds();
  return (await response.json()) as T;
}
