import { fireEvent, screen, waitFor, within } from '@testing-library/react';

import { renderWithProviders } from 'testUtils';
import type { DispatchEntry, DispatchView } from 'typings/api';

import QueuePage from './QueuePage';

const operator = { subject: 'op-1', name: 'alice', role: 'Operator', permissions: ['fleet.read', 'approvals.create'] };
const reader = { ...operator, role: 'Reader', permissions: ['fleet.read'] };

function entry(problemId: string, position: number, extra: Partial<DispatchEntry> = {}): DispatchEntry {
  return {
    position,
    problemId,
    title: `title of ${problemId}`,
    kind: 'DependencyUpdate',
    repository: 'example/shop',
    priority: 50,
    rank: 3,
    since: new Date(Date.now() - 4 * 60_000).toISOString(),
    state: 'waitingTurn',
    detail: 'express 4 → 5',
    held: null,
    next: false,
    ...extra,
  };
}

const view: DispatchView = {
  line: [
    entry('p-image', 1, { title: 'node:14-alpine → 22', kind: 'ImageUpdate', rank: 1, next: true, priority: 80 }),
    entry('p-held', 2, {
      state: 'held',
      held: { by: 'alice', at: new Date().toISOString(), reason: 'an analysis first' },
    }),
  ],
  inProcess: [
    {
      problemId: 'p-run',
      title: 'express 4 → 5',
      kind: 'DependencyUpdate',
      repository: 'example/shop',
      place: 'Staging',
      priority: 50,
    },
  ],
};

function stubQueue(session: unknown, refusal?: { status: number; error: string }) {
  const calls: { path: string; body: unknown }[] = [];
  const fetchMock = vi.fn((input: string, init?: RequestInit) => {
    const path = String(input);
    if (init?.method === 'POST') {
      calls.push({ path, body: init.body ? JSON.parse(String(init.body)) : null });
      return Promise.resolve(
        refusal
          ? new Response(JSON.stringify({ error: refusal.error }), { status: refusal.status })
          : new Response('{}', { status: 200 }),
      );
    }
    if (path === '/api/v1/flow/queue') {
      return Promise.resolve(new Response(JSON.stringify(view), { status: 200 }));
    }
    if (path === '/api/v1/auth/me') {
      return Promise.resolve(new Response(JSON.stringify(session), { status: 200 }));
    }
    return Promise.resolve(new Response('{}', { status: 404 }));
  });
  vi.stubGlobal('fetch', fetchMock);
  return calls;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('QueuePage', () => {
  it('lists the line in the order it goes, why each waits, the next of its lot and who held what', async () => {
    stubQueue(operator);

    renderWithProviders(<QueuePage />);

    const rows = within((await screen.findAllByRole('table'))[0]).getAllByRole('row');
    expect(rows[1]).toHaveTextContent('node:14-alpine → 22');
    expect(rows[1]).toHaveTextContent('next of its lot');
    expect(rows[1]).toHaveTextContent('runtime');
    expect(rows[2]).toHaveTextContent('held');
    expect(rows[2]).toHaveTextContent('Held by alice: an analysis first');
    expect(within(rows[2]).getByRole('button', { name: 'Release' })).toBeInTheDocument();
    expect(within(screen.getAllByRole('table')[1]).getByText('Staging')).toBeInTheDocument();
  });

  it('holds an operation only with a reason, and sets a priority', async () => {
    const calls = stubQueue(operator);

    renderWithProviders(<QueuePage />);

    const first = within((await screen.findAllByRole('table'))[0]).getAllByRole('row')[1];
    fireEvent.click(within(first).getByRole('button', { name: 'Hold' }));
    const hold = within(first).getAllByRole('button', { name: 'Hold' })[0];
    expect(hold).toBeDisabled();
    fireEvent.change(within(first).getByLabelText('Why hold p-image'), { target: { value: 'wait for the vendor' } });
    fireEvent.click(hold);
    fireEvent.change(within(first).getByLabelText('Priority of p-image'), { target: { value: '90' } });
    fireEvent.click(within(first).getByRole('button', { name: 'Set' }));

    await waitFor(() => expect(calls).toHaveLength(2));
    expect(calls).toContainEqual({ path: '/api/v1/flow/p-image/hold', body: { reason: 'wait for the vendor' } });
    expect(calls).toContainEqual({ path: '/api/v1/flow/p-image/priority', body: { priority: 90 } });
  });

  it('shows the line to a reader with no lever', async () => {
    stubQueue(reader);

    renderWithProviders(<QueuePage />);

    expect(await screen.findByText('node:14-alpine → 22')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Hold' })).not.toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'Set' })).not.toBeInTheDocument();
  });

  it('says next to the lever why the host refused it', async () => {
    stubQueue(operator, { status: 409, error: 'notInTheLine' });

    renderWithProviders(<QueuePage />);

    const first = within((await screen.findAllByRole('table'))[0]).getAllByRole('row')[1];
    fireEvent.click(within(first).getByRole('button', { name: 'Hold' }));
    fireEvent.change(within(first).getByLabelText('Why hold p-image'), { target: { value: 'wait for the vendor' } });
    fireEvent.click(within(first).getAllByRole('button', { name: 'Hold' })[0]);

    expect(await within(first).findByRole('alert')).toHaveTextContent(
      'Refused: the operation has started or left the line.',
    );
  });

  it('releases a held operation, and says so when the trail could not record it', async () => {
    const calls = stubQueue(operator, { status: 503, error: 'storageUnavailable' });

    renderWithProviders(<QueuePage />);

    const held = within((await screen.findAllByRole('table'))[0]).getAllByRole('row')[2];
    fireEvent.click(within(held).getByRole('button', { name: 'Release' }));

    expect(await within(held).findByRole('alert')).toHaveTextContent('Not done: the audit trail could not record it.');
    expect(calls).toContainEqual({ path: '/api/v1/flow/p-held/release', body: {} });
  });

  it('sends no priority from an empty field', async () => {
    const calls = stubQueue(operator);

    renderWithProviders(<QueuePage />);

    const first = within((await screen.findAllByRole('table'))[0]).getAllByRole('row')[1];
    fireEvent.change(within(first).getByLabelText('Priority of p-image'), { target: { value: '' } });

    expect(within(first).getByRole('button', { name: 'Set' })).toBeDisabled();
    fireEvent.submit(within(first).getByLabelText('Priority of p-image'));
    expect(calls).toHaveLength(0);
  });
});
