import { useQuery, type UseQueryResult } from '@tanstack/react-query';

import { noteSession } from 'Helpers/signIn';

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
  noteSession(response);
  if (!response.ok) {
    throw new ApiError(response.status, `${response.status} ${response.statusText}`);
  }
  return (await response.json()) as T;
}

// The only place that talks to the API: components never call fetch.
export default function useApiQuery<T>(
  path: string,
  options: {
    refetchInterval?: number | ((data: T | undefined) => number);
    // For an answer that does not change while the page is open, or that is not there at all: ask once
    once?: boolean;
  } = {},
): UseQueryResult<T, ApiError> {
  const interval = options.refetchInterval;
  return useQuery<T, ApiError>({
    queryKey: [path],
    queryFn: () => getJson<T>(path),
    refetchInterval: typeof interval === 'function' ? (query) => interval(query.state.data) : interval,
    ...(options.once ? { retry: false, staleTime: Infinity, refetchOnWindowFocus: false } : {}),
  });
}
