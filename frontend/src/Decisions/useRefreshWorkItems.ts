import { useMutation, useQueryClient } from '@tanstack/react-query';

import { getJson } from 'Helpers/Hooks/useApiQuery';
import type { WorkItemReport } from 'typings/api';

export const workItemsPath = '/api/v1/work-items';

// Reads the source again now, instead of waiting for the cache (the backend caches 30 seconds)
export default function useRefreshWorkItems() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => getJson<WorkItemReport>(`${workItemsPath}?refresh=true`),
    onSuccess: (report) => {
      queryClient.setQueryData([workItemsPath], report);
      void queryClient.invalidateQueries({ queryKey: ['/api/v1/system/status'] });
    },
  });
}
