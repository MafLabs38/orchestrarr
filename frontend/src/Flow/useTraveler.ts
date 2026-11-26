import useApiQuery, { refreshIntervalMs } from 'Helpers/Hooks/useApiQuery';
import type { TravelerView } from 'typings/api';

// The traveler of an operation: the host announces every change of the missions, so this reads again at once (the path starts like the other reads of the flow)
export function useTraveler(problemId: string) {
  return useApiQuery<TravelerView>(`/api/v1/problems/${encodeURIComponent(problemId)}/traveler`, {
    refetchInterval: refreshIntervalMs,
  });
}
