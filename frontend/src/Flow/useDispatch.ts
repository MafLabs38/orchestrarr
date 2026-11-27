import { useMutation, useQueryClient } from '@tanstack/react-query';

import sendJson from 'Helpers/Hooks/sendJson';
import useApiQuery, { refreshIntervalMs } from 'Helpers/Hooks/useApiQuery';
import type { DispatchView } from 'typings/api';

export const dispatchPath = '/api/v1/flow/queue';

// The dispatch list: the host announces every change of the flow on the missions topic, so this reads again at once
export function useDispatch() {
  return useApiQuery<DispatchView>(dispatchPath, { refetchInterval: refreshIntervalMs });
}

function useFlowAct<T>(act: (value: T) => Promise<unknown>) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: act,
    onSettled: () =>
      queryClient.invalidateQueries({ predicate: ({ queryKey }) => String(queryKey[0]).startsWith('/api/v1/flow') }),
  });
}

// The operator's two levers on the line (ADR 0056): neither grants a power, a priority orders the work and a hold delays it
export function useHold() {
  return useFlowAct(({ problemId, reason }: { problemId: string; reason: string }) =>
    sendJson('POST', `/api/v1/flow/${encodeURIComponent(problemId)}/hold`, { reason }),
  );
}

export function useRelease() {
  return useFlowAct((problemId: string) =>
    sendJson('POST', `/api/v1/flow/${encodeURIComponent(problemId)}/release`, {}),
  );
}

export function useSetPriority() {
  return useFlowAct(({ problemId, priority }: { problemId: string; priority: number }) =>
    sendJson('POST', `/api/v1/flow/${encodeURIComponent(problemId)}/priority`, { priority }),
  );
}
