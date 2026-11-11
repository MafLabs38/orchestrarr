import { useMutation, useQueryClient } from '@tanstack/react-query';

import sendJson from 'Helpers/Hooks/sendJson';
import useApiQuery, { refreshIntervalMs } from 'Helpers/Hooks/useApiQuery';
import { systemStatusPath } from 'System/useSystemStatus';
import type { Approval } from 'typings/api';

export const approvalsPath = '/api/v1/approvals';
export const auditPath = '/api/v1/audit';

export function useApprovals() {
  return useApiQuery<Approval[]>(approvalsPath, { refetchInterval: refreshIntervalMs });
}

export function useKillSwitch() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (engaged: boolean) =>
      sendJson<{ killSwitchEngaged: boolean }>('PUT', '/api/v1/safety/kill-switch', { engaged }),
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: [systemStatusPath] }),
        queryClient.invalidateQueries({ queryKey: [auditPath] }),
      ]),
  });
}
