import { useQuery, type UseQueryResult } from '@tanstack/react-query';

import { redirectToSignIn, signInHolds } from 'Helpers/signIn';

// How often the screens read again; the server keeps a read for about as long (WorkItems:CacheDuration)
export const refreshIntervalMs = 30_000;

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

export async function getJson<T>(path: string): Promise<T> {
  const response = await fetch(path, { headers: { Accept: 'application/json' } });
  if (!response.ok) {
    if (response.status === 401) {
      redirectToSignIn();
    }
    throw new ApiError(response.status, `${response.status} ${response.statusText}`);
  }
  signInHolds();
  return (await response.json()) as T;
}

// The only place that talks to the API: components never call fetch.
export default function useApiQuery<T>(
  path: string,
  options: { refetchInterval?: number | ((data: T | undefined) => number) } = {},
): UseQueryResult<T, ApiError> {
  const interval = options.refetchInterval;
  return useQuery<T, ApiError>({
    queryKey: [path],
    queryFn: () => getJson<T>(path),
    refetchInterval: typeof interval === 'function' ? (query) => interval(query.state.data) : interval,
  });
}
