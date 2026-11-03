import { useMutation, useQueryClient } from '@tanstack/react-query';

import { getJson } from 'Helpers/Hooks/useApiQuery';
import { systemStatusPath } from 'System/useSystemStatus';
import type { WorkItemReport } from 'typings/api';

import { workItemsPath } from './useWorkItems';

// Reads the source again now, instead of waiting for the cache (the server still spaces its reads, to protect the source)
export default function useRefreshWorkItems() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => getJson<WorkItemReport>(`${workItemsPath}?refresh=true`),
    onSuccess: (report) => {
      queryClient.setQueryData([workItemsPath], report);
      return queryClient.invalidateQueries({ queryKey: [systemStatusPath] });
    },
  });
}
