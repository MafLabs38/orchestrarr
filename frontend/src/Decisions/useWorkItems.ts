import useApiQuery, { refreshIntervalMs } from 'Helpers/Hooks/useApiQuery';
import type { WorkItemReport } from 'typings/api';

export const workItemsPath = '/api/v1/work-items';

export default function useWorkItems() {
  return useApiQuery<WorkItemReport>(workItemsPath, { refetchInterval: refreshIntervalMs });
}
