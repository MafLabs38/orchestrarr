import { useMutation, useQueryClient } from '@tanstack/react-query';

import sendJson from 'Helpers/Hooks/sendJson';
import useApiQuery, { refreshIntervalMs } from 'Helpers/Hooks/useApiQuery';
import type { MachinesResponse } from 'typings/api';

export const machinesPath = '/api/v1/machines';

// The lab machines and their state. The host announces a change on the `machines` topic of the live channel, so the screen follows it as it happens;
// the read every thirty seconds is only the fallback where the channel cannot be opened.
export function useMachines() {
  return useApiQuery<MachinesResponse>(machinesPath, { refetchInterval: refreshIntervalMs });
}

// A person asks for a new qualification of a machine that is down (once what was wrong is repaired) or that is finished and not taken up
export function useRequalify() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) =>
      sendJson<{ state: string }>('POST', `${machinesPath}/${encodeURIComponent(id)}/requalify`, {}),
    onSettled: () => queryClient.invalidateQueries({ queryKey: [machinesPath] }),
  });
}
