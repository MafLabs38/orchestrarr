import { screen, within } from '@testing-library/react';

import { renderWithProviders, stubApi } from 'testUtils';
import type { FlowProblem, MissionView } from 'typings/api';

import Missions from './Missions';

const mission: MissionView = {
  id: 'm1',
  problemId: 'p1',
  applicationId: 'example/shop',
  world: 'Staging',
  objective: 'Prove pull request #7 in staging',
  state: 'Accepted',
  end: 'Done',
  endReason: null,
  sendBacks: 0,
  problemState: 'InProgress',
  mandate: {
    id: 'mandate1',
    runnerId: 'runner-staging',
    source: 'PlanValidation',
    acts: [],
    issuedAt: '2026-11-21T12:00:00Z',
    expiresAt: '2026-11-21T13:00:00Z',
    revoked: true,
  },
  dossier: {
    claim: 'It builds and its tests pass',
    whatChangedAndWhy: 'one file',
    openPoints: [],
    complex: false,
    complexReason: null,
  },
  progress: ['12:00:01 Fetching example/shop pull request #7', '12:00:20 Build and tests passed'],
};

const staging = { world: 'Staging', state: 'done', missionId: 'm1', attempts: 1 } as const;
const git = { world: 'Git', state: 'running', missionId: 'm2', attempts: 1 } as const;
const release = { world: 'Release', state: 'notStarted', missionId: null, attempts: 0 } as const;

const working: FlowProblem = {
  problemId: 'p1',
  title: 'nginx:1.14 → 1.28',
  kind: 'ImageUpdate',
  repository: 'example/shop',
  state: 'inProgress',
  priority: 50,
  startedAt: '2026-11-21T11:59:00Z',
  steps: [staging, git, release],
  activity: {
    kind: 'running',
    text: '12:00:20 Merged. Waiting for the pipeline of the integration branch',
    at: new Date(Date.now() - 20_000).toISOString(),
    waiting: null,
  },
  note: null,
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Missions', () => {
  it('follows each problem: its title, where it stands in each world, what it does now and how long ago', async () => {
    stubApi({ '/api/v1/flow': [working], '/api/v1/missions?limit=200': [mission] });

    renderWithProviders(<Missions />);

    expect(await screen.findByText('nginx:1.14 → 1.28')).toBeInTheDocument();
    expect(screen.getByText('example/shop')).toBeInTheDocument();
    expect(screen.getByText('in process')).toBeInTheDocument();
    const worlds = screen.getByRole('list', { name: 'Worlds' });
    expect(within(worlds).getByText('Staging').closest('li')).toHaveTextContent('done');
    expect(within(worlds).getByText('Git').closest('li')).toHaveTextContent('running');
    expect(within(worlds).getByText('Release').closest('li')).toHaveTextContent('not started');
    expect(screen.getByText('Merged. Waiting for the pipeline of the integration branch')).toBeInTheDocument();
    expect(screen.getByText(/ago$/)).toBeInTheDocument();
  });

  it('says what a problem waits for, and that one that is urgent is urgent', async () => {
    stubApi({
      '/api/v1/flow': [
        {
          ...working,
          problemId: 'p2',
          title: 'lodash 4.17.20 → 4.17.21',
          state: 'queued',
          steps: [{ ...staging, state: 'notStarted', missionId: null, attempts: 0 }],
          activity: { kind: 'waiting', text: null, at: null, waiting: { code: 'turn', subject: 'nginx:1.14 → 1.28' } },
        },
        {
          ...working,
          problemId: 'p3',
          priority: 71,
          activity: {
            kind: 'waiting',
            text: null,
            at: null,
            waiting: { code: 'machine', subject: 'leipzig (InProcess)' },
          },
        },
      ],
      '/api/v1/missions?limit=200': [],
    });

    renderWithProviders(<Missions />);

    expect(await screen.findByText('waits for its turn')).toBeInTheDocument();
    expect(screen.getByText('waits for a machine')).toBeInTheDocument();
    expect(
      screen.getByText(/Waits for its turn on this repository: nginx:1.14 → 1.28 is being done first/),
    ).toBeInTheDocument();
    expect(screen.getByText(/Waits for a machine: leipzig \(InProcess\)/)).toBeInTheDocument();
    expect(screen.getByText('Urgent 71')).toBeInTheDocument();
    expect(screen.queryByText(/every lane of this world is at work/)).not.toBeInTheDocument();
  });

  it('says that every lane of the world is at work when a mission waits for a free place', async () => {
    stubApi({
      '/api/v1/flow': [
        {
          ...working,
          activity: {
            kind: 'waiting',
            text: null,
            at: null,
            waiting: { code: 'busy', subject: 'Git 3/3' },
          },
        },
      ],
      '/api/v1/missions?limit=200': [],
    });

    renderWithProviders(<Missions />);

    expect(
      await screen.findByText(/Waits for a free place: every lane of this world is at work \(Git 3\/3\)/),
    ).toBeInTheDocument();
  });

  it('tells the alerts of a row as what they are, not as a bare number', async () => {
    stubApi({
      '/api/v1/flow': [{ ...working, kind: 'DependencyAlert', title: '2' }],
      '/api/v1/missions?limit=200': [],
    });

    renderWithProviders(<Missions />);

    expect(await screen.findByText('2 dependency alerts')).toBeInTheDocument();
  });

  it('tells a mission that has gone quiet', async () => {
    stubApi({
      '/api/v1/flow': [
        {
          ...working,
          activity: {
            kind: 'running',
            text: 'Building',
            at: new Date(Date.now() - 12 * 60_000).toISOString(),
            waiting: null,
          },
        },
      ],
      '/api/v1/missions?limit=200': [],
    });

    renderWithProviders(<Missions />);

    expect(await screen.findByText(/No news for 12 min/)).toBeInTheDocument();
  });

  it('shows the missions behind a problem: the dossier, what the runner reported and why one ended', async () => {
    stubApi({
      '/api/v1/flow': [working],
      '/api/v1/missions?limit=200': [
        mission,
        {
          ...mission,
          id: 'm3',
          world: 'Git',
          state: 'Ended',
          end: 'Refused',
          endReason: 'a pipeline file changed',
          progress: null,
        },
      ],
    });

    renderWithProviders(<Missions />);

    expect(await screen.findByText('Missions of this problem (2)')).toBeInTheDocument();
    expect(screen.getAllByText('It builds and its tests pass')).toHaveLength(2);
    expect(screen.getByText(/Build and tests passed/)).toBeInTheDocument();
    expect(screen.getAllByText(/mandate for runner-staging/)).toHaveLength(2);
    expect(screen.getByText('a pipeline file changed')).toBeInTheDocument();
  });

  it('says when nothing is being done', async () => {
    stubApi({ '/api/v1/flow': [], '/api/v1/missions?limit=200': [] });

    renderWithProviders(<Missions />);

    expect(await screen.findByText(/Nothing is being done and nothing waits/)).toBeInTheDocument();
  });

  it('says when the problems cannot be read', async () => {
    stubApi({});

    renderWithProviders(<Missions />);

    expect(await screen.findByText(/Cannot read the problems being followed/)).toBeInTheDocument();
  });
});
