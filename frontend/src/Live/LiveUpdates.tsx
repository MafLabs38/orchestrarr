import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { type QueryClient, useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';

import useSession from 'Account/useSession';

import { liveStatus } from './liveStatus';

export const livePath = '/api/v1/live';

// What each topic the host announces makes the screens read again. A key is the path of the query; a run's detail starts with the same path.
const topicPaths: Readonly<Record<string, readonly string[]>> = {
  // What a run ended with changes what the table offers for that pull request
  runs: ['/api/v1/runs', '/api/v1/problems'],
  approvals: ['/api/v1/approvals'],
  audit: ['/api/v1/audit'],
  status: ['/api/v1/system/status'],
  problems: ['/api/v1/problems', '/api/v1/work-items'],
  accounts: ['/api/v1/accounts', '/api/v1/auth/me'],
  options: ['/api/v1/options', '/api/v1/system/status'],
};

export function invalidateTopic(queryClient: QueryClient, topic: string, id?: string | null) {
  const paths = topicPaths[topic];
  if (!paths) {
    return Promise.resolve();
  }
  return queryClient.invalidateQueries({
    predicate: ({ queryKey }) => {
      const key = String(queryKey[0]);
      // A run that is told by its id only reads that one (and the list); the others have not changed
      if (topic === 'runs' && id && key.startsWith('/api/v1/runs/') && !key.endsWith(id)) {
        return false;
      }
      return paths.some((path) => key.startsWith(path));
    },
  });
}

const retryAfterMs = 10_000;

// Opens the WebSocket of the host and turns what it announces into fresh reads. It carries no data, only "this changed": the answer comes
// through the API, with the session, like any other read. Where the WebSocket cannot be opened (not signed in, a proxy that refuses it) the
// screens keep reading every thirty seconds, and the badge says so.
export default function LiveUpdates() {
  const queryClient = useQueryClient();
  // The channel is for a person who signed in: it opens as soon as there is a session, and closes when there is none
  const signedIn = useSession().data !== undefined;

  useEffect(() => {
    if (!signedIn) {
      liveStatus.set('connecting');
      return undefined;
    }
    let stopped = false;
    let retry: ReturnType<typeof setTimeout> | undefined;
    const connection = new HubConnectionBuilder()
      .withUrl(livePath, { headers: { 'X-Orchestrarr-Request': 'live' } })
      .withAutomaticReconnect([0, 1000, 2000, 5000, 10_000, 30_000])
      .configureLogging(LogLevel.None)
      .build();

    connection.on('changed', (topic: string, id: string | null) => {
      void invalidateTopic(queryClient, topic, id);
    });
    connection.onreconnecting(() => liveStatus.set('reconnecting'));
    connection.onreconnected(() => {
      liveStatus.set('live');
      // What changed while it was down was not announced: read everything again
      void queryClient.invalidateQueries();
    });
    connection.onclose(() => {
      if (!stopped) {
        liveStatus.set('off');
        retry = setTimeout(() => void open(), retryAfterMs);
      }
    });

    async function open() {
      if (stopped || connection.state !== HubConnectionState.Disconnected) {
        return;
      }
      try {
        await connection.start();
        liveStatus.set('live');
        void queryClient.invalidateQueries();
      } catch {
        liveStatus.set('off');
        if (!stopped) {
          retry = setTimeout(() => void open(), retryAfterMs);
        }
      }
    }

    void open();
    return () => {
      stopped = true;
      clearTimeout(retry);
      void connection.stop();
    };
  }, [queryClient, signedIn]);

  return null;
}
