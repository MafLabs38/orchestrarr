import useApiQuery, { refreshIntervalMs } from 'Helpers/Hooks/useApiQuery';
import type { RunDetail, RunSummary } from 'typings/api';

export const runsPath = '/api/v1/runs';

export function useRuns() {
  return useApiQuery<RunSummary[]>(runsPath, { refetchInterval: refreshIntervalMs });
}

export function useRun(id: string) {
  return useApiQuery<RunDetail>(`${runsPath}/${id}`, { refetchInterval: refreshIntervalMs });
}
