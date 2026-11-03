import useApiQuery, { refreshIntervalMs } from 'Helpers/Hooks/useApiQuery';
import type { WorkItemReport } from 'typings/api';

export const workItemsPath = '/api/v1/work-items';

// While the pull requests of some work item are still being read, read again soon to get them
const pendingIntervalMs = 3000;

export function intervalFor(report: WorkItemReport | undefined): number {
  return report?.items.some((item) => item.pullRequests.status === 'pending') ? pendingIntervalMs : refreshIntervalMs;
}

export default function useWorkItems() {
  return useApiQuery<WorkItemReport>(workItemsPath, { refetchInterval: intervalFor });
}
