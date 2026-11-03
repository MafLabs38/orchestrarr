import useApiQuery, { refreshIntervalMs } from 'Helpers/Hooks/useApiQuery';
import type { SystemStatus } from 'typings/api';

export const systemStatusPath = '/api/v1/system/status';

export default function useSystemStatus() {
  return useApiQuery<SystemStatus>(systemStatusPath, { refetchInterval: refreshIntervalMs });
}
