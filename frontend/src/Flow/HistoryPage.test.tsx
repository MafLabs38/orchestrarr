import { fireEvent, screen, within } from '@testing-library/react';

import { renderWithProviders, stubApi } from 'testUtils';
import type { FlowBoard, TravelerView } from 'typings/api';

import HistoryPage from './HistoryPage';

const board: FlowBoard = {
  places: ['queue', 'Staging', 'Git', 'out'],
  workshops: [],
  lots: [
    {
      repository: 'example/shop',
      operations: [
        {
          problemId: 'p-grain',
          title: '1',
          kind: 'DependencyAlert',
          repository: 'example/shop',
          place: 'out',
          state: 'putAside',
          detail: 'no fixed version exists',
          priority: 50,
          since: '2026-11-26T09:00:00Z',
          equipment: null,
        },
        {
          problemId: 'p-image',
          title: 'node:14-alpine → 22',
          kind: 'ImageUpdate',
          repository: 'example/shop',
          place: 'Staging',
          state: 'working',
          detail: null,
          priority: 50,
          since: '2026-11-26T09:10:00Z',
          equipment: 'leipzig',
        },
      ],
    },
  ],
  counters: { queued: 0, inProcess: 1, waiting: 0, done: 0, putAside: 1, oldestQueuedSince: null, held: 0 },
};

const traveler: TravelerView = {
  problemId: 'p-grain',
  title: '1',
  kind: 'DependencyAlert',
  repository: 'example/shop',
  state: 'escalated',
  priority: 50,
  note: 'no fixed version exists',
  steps: [
    { at: '2026-11-26T09:00:00Z', event: 'problem.queued', world: null, text: 'waits for its turn', times: 1 },
    {
      at: '2026-11-26T09:01:00Z',
      event: 'act.refused',
      world: 'Git',
      text: 'MergePullRequest: CiResultNotKnown',
      times: 3,
    },
    { at: '2026-11-26T09:05:00Z', event: 'problem.escalated', world: null, text: 'a person decides', times: 1 },
  ],
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('HistoryPage', () => {
  it('lists the operations, what is going on first, and opens the traveler of the one chosen step by step', async () => {
    stubApi({ '/api/v1/flow/board': board, '/api/v1/problems/p-grain/traveler': traveler });

    renderWithProviders(<HistoryPage />);

    const list = await screen.findByRole('navigation', { name: 'Operations' });
    const links = await within(list).findAllByRole('link');
    expect(links[0]).toHaveTextContent('node:14-alpine → 22');
    expect(links[1]).toHaveTextContent('1 dependency alerts');
    expect(screen.getByText('Choose an operation to read its traveler.')).toBeInTheDocument();

    fireEvent.click(links[1]);

    const opened = await screen.findByRole('region', { name: 'Traveler' });
    expect(within(opened).getByText('in the line')).toBeInTheDocument();
    expect(within(opened).getByText('act not allowed now')).toBeInTheDocument();
    expect(within(opened).getByText('× 3')).toBeInTheDocument();
    expect(within(opened).getByText('Forge')).toBeInTheDocument();
    expect(within(opened).getByText('escalated to a person')).toBeInTheDocument();
    expect(within(opened).getAllByText(/no fixed version exists/).length).toBeGreaterThan(0);
  });
});
