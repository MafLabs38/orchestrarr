import { screen, within } from '@testing-library/react';

import { renderWithProviders, stubApi } from 'testUtils';
import type { BoardOperation, FlowBoard } from 'typings/api';

import FlowPage from './FlowPage';

const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

function operation(
  problemId: string,
  place: string,
  state: BoardOperation['state'],
  extra: Partial<BoardOperation> = {},
): BoardOperation {
  return {
    problemId,
    title: `title of ${problemId}`,
    kind: 'DependencyUpdate',
    repository: 'orchestrarr-sandbox/shop-api',
    place,
    state,
    detail: null,
    priority: 50,
    since: minutesAgo(1),
    equipment: null,
    ...extra,
  };
}

const board: FlowBoard = {
  places: ['queue', 'Staging', 'Git', 'out'],
  workshops: [
    {
      world: 'Staging',
      equipment: [
        {
          id: 'leipzig',
          kind: 'machine',
          state: 'inProcess',
          problemId: 'p-1',
          title: 'node:14-alpine → 22',
          repository: 'orchestrarr-sandbox/shop-api',
          since: minutesAgo(2),
          reason: null,
        },
        {
          id: 'dresden',
          kind: 'machine',
          state: 'down',
          problemId: null,
          title: null,
          repository: null,
          since: minutesAgo(9),
          reason: 'the qualification failed: disk',
        },
      ],
      inProcess: 1,
      waiting: 1,
      why: ['waitingMachine'],
    },
    {
      world: 'Git',
      equipment: [
        {
          id: 'slot-1',
          kind: 'slot',
          state: 'idle',
          problemId: null,
          title: null,
          repository: null,
          since: null,
          reason: null,
        },
      ],
      inProcess: 0,
      waiting: 0,
      why: [],
    },
  ],
  lots: [
    {
      repository: 'orchestrarr-sandbox/shop-api',
      operations: [
        operation('p-1', 'Staging', 'working', { equipment: 'leipzig', title: 'node:14-alpine → 22' }),
        operation('p-2', 'queue', 'waitingBot', { title: 'mocha 8.0.1 → 10.8.2' }),
        operation('p-3', 'out', 'putAside', { title: '1', kind: 'DependencyAlert', detail: 'no fixed version exists' }),
      ],
    },
    {
      repository: 'orchestrarr-sandbox/ledger-worker',
      operations: [
        operation('p-4', 'Staging', 'waitingMachine', {
          repository: 'orchestrarr-sandbox/ledger-worker',
          priority: 70,
        }),
      ],
    },
  ],
  counters: { queued: 1, inProcess: 1, waiting: 1, done: 4, putAside: 1, oldestQueuedSince: minutesAgo(3) },
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('FlowPage', () => {
  it('shows the workshops with their equipment, its state in words, what it works on and the buffer in front', async () => {
    stubApi({ '/api/v1/flow/board': board });

    renderWithProviders(<FlowPage />);

    const staging = await screen.findByRole('region', { name: 'Staging' });
    expect(within(staging).getByLabelText('leipzig: in process')).toHaveTextContent('node:14-alpine → 22');
    expect(within(staging).getByLabelText('dresden: down')).toHaveTextContent('the qualification failed: disk');
    expect(within(staging).getByText('1 in process · 1 waiting')).toBeInTheDocument();
    expect(within(staging).getByText('1 waiting in front: waits for a machine')).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Forge' })).getByLabelText('slot-1: idle')).toBeInTheDocument();
    expect(
      within(screen.getByRole('region', { name: 'Line' })).getByText(/the oldest waits for 3 min/),
    ).toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Out' })).getByText('4 done')).toBeInTheDocument();
  });

  it('places each operation of a lot in the column where it is, with what it does in words', async () => {
    stubApi({ '/api/v1/flow/board': board });

    renderWithProviders(<FlowPage />);

    const staging = await screen.findByRole('cell', { name: 'orchestrarr-sandbox/shop-api · Staging' });
    expect(within(staging).getByText('node:14-alpine → 22')).toBeInTheDocument();
    expect(within(staging).getByText(/in process · leipzig/)).toBeInTheDocument();
    expect(
      within(screen.getByRole('cell', { name: 'orchestrarr-sandbox/shop-api · Line' })).getByText(/waits for its bot/),
    ).toBeInTheDocument();
    const out = screen.getByRole('cell', { name: 'orchestrarr-sandbox/shop-api · Out' });
    expect(within(out).getByText('1 dependency alerts')).toBeInTheDocument();
    expect(within(out).getByText(/put aside/)).toBeInTheDocument();
    expect(
      within(screen.getByRole('cell', { name: 'orchestrarr-sandbox/ledger-worker · Staging' })).getByText(
        /waits for a machine · Urgent 70/,
      ),
    ).toBeInTheDocument();
    expect(screen.getByRole('cell', { name: 'orchestrarr-sandbox/ledger-worker · Forge' })).toBeEmptyDOMElement();
  });

  it('says that the flow is empty when no operation was validated', async () => {
    stubApi({ '/api/v1/flow/board': { ...board, lots: [] } });

    renderWithProviders(<FlowPage />);

    expect(await screen.findByText(/Nothing is being done and nothing waits/)).toBeInTheDocument();
  });
});
