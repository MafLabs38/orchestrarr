import { useMutation, useQueryClient } from '@tanstack/react-query';

import sendJson from 'Helpers/Hooks/sendJson';
import useApiQuery, { refreshIntervalMs } from 'Helpers/Hooks/useApiQuery';
import { systemStatusPath } from 'System/useSystemStatus';
import type { Approval, StepKind } from 'typings/api';

export const approvalsPath = '/api/v1/approvals';
export const activityPath = '/api/v1/activity';

export function useApprovals() {
  return useApiQuery<Approval[]>(approvalsPath, { refetchInterval: refreshIntervalMs });
}

// Approves a decision card: the server decides the commits, the repository and the powers; the client only says which steps it ticked.
export function useApprove() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: {
      workItemId: string;
      steps: StepKind[];
      pullRequests: { number: number; headSha: string }[];
    }) => sendJson<{ approvals: Approval[] }>('POST', approvalsPath, request),
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: [approvalsPath] }),
        queryClient.invalidateQueries({ queryKey: [activityPath] }),
        queryClient.invalidateQueries({ queryKey: [systemStatusPath] }),
      ]),
  });
}

export function useKillSwitch() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (engaged: boolean) =>
      sendJson<{ killSwitchEngaged: boolean }>('PUT', '/api/v1/safety/kill-switch', { engaged }),
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: [systemStatusPath] }),
        queryClient.invalidateQueries({ queryKey: [activityPath] }),
      ]),
  });
}
