import { fireEvent, screen } from '@testing-library/react';

import { renderWithProviders, stubApi } from 'testUtils';
import type { MachinesResponse, MachineView } from 'typings/api';

import MachineBoard from './MachineBoard';

const stats = {
  from: '2026-11-21T10:00:00Z',
  to: '2026-11-22T10:00:00Z',
  availability: 0.9,
  utilisation: 0.25,
  lots: 4,
  meanLotSeconds: 300,
  downEvents: 1,
  meanTimeToRepairSeconds: 600,
  secondsInState: {},
};

const idle: MachineView = {
  id: 'leipzig',
  role: 'Staging',
  state: 'Idle',
  since: new Date(Date.now() - 5 * 60_000).toISOString(),
  missionId: null,
  reason: null,
  runner: 'runner-staging',
  qualifiedAt: '2026-11-22T09:55:00Z',
  qualificationPassed: true,
  checks: [
    { name: 'isolation', passed: true, detail: '5 address(es) of the network are out of reach' },
    { name: 'routes', passed: true, detail: 'the registries and the forge answer' },
  ],
  stats,
};

function machines(list: MachineView[]): MachinesResponse {
  return { machines: list, windowHours: 24 };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MachineBoard', () => {
  it('shows each machine with its role, its state in words, how long, what it passed and how it has been used', async () => {
    stubApi({
      '/api/v1/machines': machines([
        idle,
        {
          ...idle,
          id: 'bilbao',
          role: 'Release',
          state: 'InProcess',
          missionId: 'm-17',
          since: new Date(Date.now() - 90_000).toISOString(),
        },
      ]),
    });

    renderWithProviders(<MachineBoard />);

    expect(await screen.findByText('leipzig')).toBeInTheDocument();
    expect(screen.getByText('Idle')).toBeInTheDocument();
    expect(screen.getByText('staging')).toBeInTheDocument();
    expect(screen.getByText(/for 5 min/)).toBeInTheDocument();
    expect(screen.getAllByText(/isolation: 5 address\(es\) of the network are out of reach/).length).toBe(2);
    expect(
      screen.getAllByText(
        /Observed for 24 h 0 min: available 90%, in use 25%, 4 lot\(s\), 1 time\(s\) down, mean time to repair 10 min/,
      ).length,
    ).toBe(2);
    expect(screen.getByText('In process')).toBeInTheDocument();
    expect(screen.getByText('release')).toBeInTheDocument();
    expect(screen.getByText('held by mission m-17')).toBeInTheDocument();
  });

  it('says why a machine is down, shows the check that failed, and lets a person qualify it again', async () => {
    const down: MachineView = {
      ...idle,
      state: 'Down',
      reason: 'isolation: it reaches 192.168.1.10:5180',
      qualificationPassed: false,
      checks: [{ name: 'isolation', passed: false, detail: 'it reaches 192.168.1.10:5180' }],
    };
    const calls = stubApi({
      '/api/v1/machines': machines([down]),
      '/api/v1/machines/leipzig/requalify': { state: 'Qualifying' },
    });

    renderWithProviders(<MachineBoard />);

    expect(await screen.findByText('Down')).toBeInTheDocument();
    expect(screen.getByText('Not fit to work: isolation: it reaches 192.168.1.10:5180')).toBeInTheDocument();
    expect(screen.getByText('isolation FAILED: it reaches 192.168.1.10:5180')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Qualify again' }));
    await vi.waitFor(() =>
      expect(
        (calls.mock.calls as unknown as [string, RequestInit | undefined][]).some(
          ([url, init]) => url === '/api/v1/machines/leipzig/requalify' && init?.method === 'POST',
        ),
      ).toBe(true),
    );
  });

  it('offers no new qualification for a machine that is idle or in process, and says when it was never qualified', async () => {
    stubApi({
      '/api/v1/machines': machines([
        idle,
        { ...idle, id: 'bilbao', state: 'Qualifying', checks: [], qualifiedAt: null, qualificationPassed: null },
      ]),
    });

    renderWithProviders(<MachineBoard />);

    expect(await screen.findByText('Qualifying')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Qualify again' })).not.toBeInTheDocument();
    expect(screen.getByText('Not qualified yet')).toBeInTheDocument();
  });

  it('shows nothing where no machine is configured, and says when the machines cannot be read', async () => {
    stubApi({ '/api/v1/machines': machines([]) });
    const { container } = renderWithProviders(<MachineBoard />);
    await vi.waitFor(() => expect(container).toBeEmptyDOMElement());

    vi.unstubAllGlobals();
    stubApi({});
    renderWithProviders(<MachineBoard />);
    expect(await screen.findByText(/The machines cannot be read/)).toBeInTheDocument();
  });
});
