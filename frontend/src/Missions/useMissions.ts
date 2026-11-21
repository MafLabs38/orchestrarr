import { useMutation, useQueryClient } from '@tanstack/react-query';

import { problemsPath } from 'Decisions/useProblems';
import sendJson from 'Helpers/Hooks/sendJson';
import useApiQuery, { refreshIntervalMs } from 'Helpers/Hooks/useApiQuery';
import type { MissionView, PlanStatus, ValidatedProblem } from 'typings/api';

export const missionsPath = '/api/v1/missions';
export const planPath = '/api/v1/plan';

export function useMissions() {
  return useApiQuery<MissionView[]>(missionsPath, { refetchInterval: refreshIntervalMs });
}

// Whether this host validates a selection through the missions (the answer does not change while the page is open)
export function usePlanStatus() {
  return useApiQuery<PlanStatus>(planPath, { once: true });
}

// Validates the selection as problems of the conductor: each ticked row, at the commit that was seen, becomes a problem and its first mission
export function useValidatePlan() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (rows: { id: string; headSha: string; stopBeforeProduction: boolean }[]) =>
      sendJson<{ problems: ValidatedProblem[] }>('POST', `${planPath}/validate`, { rows }),
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: [missionsPath] }),
        queryClient.invalidateQueries({ queryKey: [problemsPath] }),
      ]),
  });
}
