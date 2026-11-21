import { screen } from '@testing-library/react';

import { renderWithProviders, stubApi } from 'testUtils';
import type { MissionView } from 'typings/api';

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

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Missions', () => {
  it('shows each mission with its world, its state in words, its dossier and what its runner reported', async () => {
    stubApi({
      '/api/v1/missions': [
        mission,
        {
          ...mission,
          id: 'm2',
          world: 'Git',
          state: 'Mandated',
          end: null,
          progress: [],
          dossier: null,
          mandate: { ...mission.mandate, runnerId: 'runner-git', acts: ['MergePullRequest'], revoked: false },
        },
        { ...mission, id: 'm3', state: 'Ended', end: 'Refused', endReason: 'a pipeline file changed', progress: null },
      ],
    });

    renderWithProviders(<Missions />);

    expect(await screen.findByText('Accepted')).toBeInTheDocument();
    expect(screen.getByText('Waiting for its runner')).toBeInTheDocument();
    expect(screen.getByText('Refused')).toBeInTheDocument();
    expect(screen.getAllByText('It builds and its tests pass').length).toBeGreaterThan(0);
    expect(screen.getByText(/Build and tests passed/)).toBeInTheDocument();
    expect(screen.getByText(/mandate for runner-git, acts: MergePullRequest/)).toBeInTheDocument();
    expect(screen.getByText('a pipeline file changed')).toBeInTheDocument();
  });

  it('says when there is no mission yet', async () => {
    stubApi({ '/api/v1/missions': [] });

    renderWithProviders(<Missions />);

    expect(await screen.findByText(/No mission yet/)).toBeInTheDocument();
  });

  it('says when the missions cannot be read', async () => {
    stubApi({});

    renderWithProviders(<Missions />);

    expect(await screen.findByText(/Cannot read the missions/)).toBeInTheDocument();
  });
});
