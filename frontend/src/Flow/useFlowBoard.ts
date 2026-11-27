import useApiQuery, { refreshIntervalMs } from 'Helpers/Hooks/useApiQuery';
import type { FlowBoard } from 'typings/api';

export const flowBoardPath = '/api/v1/flow/board';

// The factory view: the host announces every change of the missions and of the machines, so this reads again at once; the interval is only a fallback
export function useFlowBoard() {
  return useApiQuery<FlowBoard>(flowBoardPath, { refetchInterval: refreshIntervalMs });
}
