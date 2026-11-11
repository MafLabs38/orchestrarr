import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { stubApi } from 'testUtils';

import { liveStatus } from './liveStatus';
import LiveUpdates, { invalidateTopic } from './LiveUpdates';

// A connection that does what the client does, without a network: it remembers what the component asked of it
const fake = vi.hoisted(() => {
  const state = {
    handlers: {} as Record<string, (...args: unknown[]) => void>,
    start: vi.fn(),
    stop: vi.fn(),
    connection: {
      state: 'Disconnected',
      on: (name: string, handler: (...args: unknown[]) => void) => {
        state.handlers[name] = handler;
      },
      onreconnecting: (handler: () => void) => {
        state.handlers.reconnecting = handler;
      },
      onreconnected: (handler: () => void) => {
        state.handlers.reconnected = handler;
      },
      onclose: (handler: () => void) => {
        state.handlers.close = handler;
      },
      start: () => state.start(),
      stop: () => state.stop(),
    },
    url: '',
    options: {} as { headers?: Record<string, string> },
  };
  return state;
});

vi.mock('@microsoft/signalr', () => ({
  HubConnectionState: { Disconnected: 'Disconnected' },
  LogLevel: { None: 6 },
  HubConnectionBuilder: class {
    withUrl(url: string, options: { headers?: Record<string, string> }) {
      fake.url = url;
      fake.options = options;
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      return fake.connection;
    }
  },
}));

const session = { subject: 's-1', name: 'alice', role: 'Operator', permissions: ['fleet.read'] };

function setup() {
  stubApi({ '/api/v1/auth/me': session });
  const queryClient = new QueryClient();
  for (const key of [
    '/api/v1/runs',
    '/api/v1/runs/run-1',
    '/api/v1/runs/run-2',
    '/api/v1/approvals',
    '/api/v1/audit',
    '/api/v1/system/status',
    '/api/v1/problems',
  ]) {
    queryClient.setQueryData([key], {});
  }
  const stale = () =>
    queryClient
      .getQueryCache()
      .findAll()
      .filter((query) => query.state.isInvalidated)
      .map((query) => String(query.queryKey[0]))
      .sort((a, b) => a.localeCompare(b));
  return { queryClient, stale };
}

beforeEach(() => {
  fake.start.mockReset();
  fake.stop.mockReset();
  fake.handlers = {};
  liveStatus.set('connecting');
});

afterEach(() => {
  vi.useRealTimers();
});

describe('invalidateTopic', () => {
  it('makes the screens that show what changed read again, and only those', async () => {
    const { queryClient, stale } = setup();

    await invalidateTopic(queryClient, 'approvals');
    expect(stale()).toEqual(['/api/v1/approvals']);

    await invalidateTopic(queryClient, 'audit');
    await invalidateTopic(queryClient, 'status');
    expect(stale()).toEqual(['/api/v1/approvals', '/api/v1/audit', '/api/v1/system/status']);
  });

  it('reads again the list of runs and the detail of the run that changed, not the others', async () => {
    const { queryClient, stale } = setup();

    await invalidateTopic(queryClient, 'runs', 'run-1');

    expect(stale()).toEqual(['/api/v1/problems', '/api/v1/runs', '/api/v1/runs/run-1']);
  });

  it('reads every run again when the change does not say which', async () => {
    const { queryClient, stale } = setup();

    await invalidateTopic(queryClient, 'runs');

    expect(stale()).toEqual(['/api/v1/problems', '/api/v1/runs', '/api/v1/runs/run-1', '/api/v1/runs/run-2']);
  });

  it('reads again the accounts and the options when they change', async () => {
    const { queryClient, stale } = setup();
    queryClient.setQueryData(['/api/v1/accounts'], {});
    queryClient.setQueryData(['/api/v1/options'], {});

    await invalidateTopic(queryClient, 'accounts');
    await invalidateTopic(queryClient, 'options');

    expect(stale()).toEqual(['/api/v1/accounts', '/api/v1/options', '/api/v1/system/status']);
  });

  it('ignores a topic it does not know', async () => {
    const { queryClient, stale } = setup();

    await invalidateTopic(queryClient, 'something-new');

    expect(stale()).toEqual([]);
  });
});

describe('LiveUpdates', () => {
  it('opens the channel with the header the host asks of a write, and says that it is live', async () => {
    fake.start.mockResolvedValue(undefined);
    const { queryClient } = setup();

    render(
      <QueryClientProvider client={queryClient}>
        <LiveUpdates />
      </QueryClientProvider>,
    );

    await vi.waitFor(() => expect(liveStatus.get()).toBe('live'));
    expect(fake.url).toBe('/api/v1/live');
    expect(fake.options.headers).toHaveProperty('X-Orchestrarr-Request');
  });

  it('reads again what the host says changed', async () => {
    fake.start.mockResolvedValue(undefined);
    const { queryClient, stale } = setup();
    render(
      <QueryClientProvider client={queryClient}>
        <LiveUpdates />
      </QueryClientProvider>,
    );
    await vi.waitFor(() => expect(liveStatus.get()).toBe('live'));
    // Opening reads everything once; start from a clean state
    await queryClient.resetQueries();
    for (const key of ['/api/v1/runs', '/api/v1/runs/run-1', '/api/v1/approvals', '/api/v1/problems']) {
      queryClient.setQueryData([key], {});
    }

    fake.handlers.changed?.('runs', 'run-1');

    await vi.waitFor(() => expect(stale()).toEqual(['/api/v1/problems', '/api/v1/runs', '/api/v1/runs/run-1']));
  });

  it('says it is reconnecting while the channel is down, and reads everything again when it is back', async () => {
    fake.start.mockResolvedValue(undefined);
    const { queryClient, stale } = setup();
    render(
      <QueryClientProvider client={queryClient}>
        <LiveUpdates />
      </QueryClientProvider>,
    );
    await vi.waitFor(() => expect(liveStatus.get()).toBe('live'));

    fake.handlers.reconnecting?.();
    expect(liveStatus.get()).toBe('reconnecting');
    fake.handlers.reconnected?.();

    expect(liveStatus.get()).toBe('live');
    await vi.waitFor(() => expect(stale().length).toBeGreaterThan(3));
  });

  it('does not open the channel while nobody has signed in, and opens it as soon as somebody does', async () => {
    fake.start.mockResolvedValue(undefined);
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response('{}', { status: 401 }))),
    );
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <QueryClientProvider client={queryClient}>
        <LiveUpdates />
      </QueryClientProvider>,
    );
    await vi.waitFor(() => expect(queryClient.getQueryState(['/api/v1/auth/me'])?.status).toBe('error'));
    expect(fake.start).not.toHaveBeenCalled();

    queryClient.setQueryData(['/api/v1/auth/me'], session);

    await vi.waitFor(() => expect(liveStatus.get()).toBe('live'));
    expect(fake.start).toHaveBeenCalledTimes(1);
  });

  it('says it is not live when it cannot connect, tries again later and stops when the page goes', async () => {
    vi.useFakeTimers();
    fake.start.mockRejectedValueOnce(new Error('401')).mockResolvedValue(undefined);
    const { queryClient } = setup();

    const view = render(
      <QueryClientProvider client={queryClient}>
        <LiveUpdates />
      </QueryClientProvider>,
    );
    await vi.advanceTimersByTimeAsync(0);
    expect(liveStatus.get()).toBe('off');

    await vi.advanceTimersByTimeAsync(10_000);
    expect(liveStatus.get()).toBe('live');

    view.unmount();
    expect(fake.stop).toHaveBeenCalled();
  });
});
