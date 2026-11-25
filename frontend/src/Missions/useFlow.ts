import { useEffect, useState } from 'react';

import useApiQuery, { refreshIntervalMs } from 'Helpers/Hooks/useApiQuery';
import type { FlowProblem } from 'typings/api';

export const flowPath = '/api/v1/flow';

// What the Missions screen follows: one entry per problem. The host announces every change on the missions topic, so this reads again at once; the interval is only a fallback.
export function useFlow() {
  return useApiQuery<FlowProblem[]>(flowPath, { refetchInterval: refreshIntervalMs });
}

// The time, read again every few seconds: "12 s ago" has to move while nothing else does (a mission that has gone quiet sends nothing)
export function useNow(everyMs = 5000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), everyMs);
    return () => clearInterval(timer);
  }, [everyMs]);
  return now;
}
