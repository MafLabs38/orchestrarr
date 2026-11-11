import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { problem, problemsReport, renderWithProviders, status } from 'testUtils';
import type { ProblemRow, ProblemsReport } from 'typings/api';

import Decisions from './Decisions';

const head = (letter: string) => letter.repeat(40);
const rows: ProblemRow[] = [
  problem(),
  problem({
    id: 'pr:example/shop#1',
    description: 'lodash 4.17.4 → 4.17.21',
    updateClass: 'patch',
    checks: 'failing',
    goesUpTo: 'qualification',
    why: 'checksFailing',
    canStopBeforeProduction: false,
    pullRequest: 1,
    headSha: head('b'),
  }),
  problem({
    id: 'pr:example/web-portal#4',
    application: 'example/web-portal',
    workItemId: 'repository:example/web-portal',
    description: 'react 17.0.2 → 18.3.1',
    updateClass: 'major',
    goesUpTo: 'mergeIntoDevelop',
    why: 'updateClassStopsBeforeProduction',
    canStopBeforeProduction: false,
    pullRequest: 4,
    headSha: head('c'),
  }),
  problem({
    id: 'finding:repository:example/api:SecretScanningAlerts',
    workItemId: 'repository:example/api',
    application: 'example/api',
    kind: 'exposedSecret',
    description: '1',
    severity: 'danger',
    count: 1,
    updateClass: null,
    checks: null,
    url: null,
    pullRequest: null,
    headSha: null,
    goesUpTo: 'noAction',
    why: 'noActionDefined',
    canStopBeforeProduction: false,
  }),
];

function stub(
  report: ProblemsReport = problemsReport(rows),
  approvals: Record<string, unknown> = {},
  refused?: Response,
) {
  const fetchMock = vi.fn((input: string, init?: RequestInit) => {
    const path = String(input);
    const method = init?.method ?? 'GET';
    if (method === 'POST' && path === '/api/v1/problems/approve') {
      return Promise.resolve(refused ?? new Response(JSON.stringify({ approvals: [{}, {}] }), { status: 201 }));
    }
    const bodies: Record<string, unknown> = {
      '/api/v1/problems': report,
      '/api/v1/system/status': status({}, { requiresSecondFactor: true, ...approvals }),
    };
    return Promise.resolve(
      path in bodies
        ? new Response(JSON.stringify(bodies[path]), { status: 200 })
        : new Response('{}', { status: 404 }),
    );
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

const posted = (fetchMock: ReturnType<typeof stub>) => {
  const call = fetchMock.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === 'POST');
  return call
    ? (JSON.parse(String((call[1] as RequestInit).body)) as {
        rows: unknown[];
        secondFactorCode?: string;
        descriptionsVersion?: string;
      })
    : undefined;
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Decisions', () => {
  it('shows one numbered line per problem: what it is, the forge and the workshop, the plan step by step and why in a few words', async () => {
    stub();

    renderWithProviders(<Decisions />);

    expect(await screen.findByText('express 4.16.0 → 4.21.2')).toBeInTheDocument();
    const lines = within(screen.getByRole('table')).getAllByRole('row').slice(1);
    expect(lines).toHaveLength(4);
    expect(lines[0]).toHaveTextContent(/^1.*shop.*Update.*express 4.16.0 → 4.21.2.*1Validate2Merge3Prod.*Whole chain/);
    expect(lines[1]).toHaveTextContent(/^2.*lodash 4.17.4 → 4.17.21.*1Validate.*CI red/);
    expect(lines[2]).toHaveTextContent(/^3.*web-portal.*react 17.0.2 → 18.3.1.*1Validate2Merge.*Stops at develop/);
    expect(lines[3]).toHaveTextContent(/^4.*api.*Secret.*No action.*No task yet/);
  });

  it('puts the long reason in a bubble beside the short one, so that a line stays one line', async () => {
    stub();
    renderWithProviders(<Decisions />);
    await screen.findByText('express 4.16.0 → 4.21.2');
    expect(screen.queryByText(/The checks fail on the forge/)).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /Why: example\/shop lodash/ }));

    expect(screen.getByRole('note')).toHaveTextContent(/The checks fail on the forge for this pull request/);
  });

  it('cannot select a problem that has no action', async () => {
    stub();
    renderWithProviders(<Decisions />);

    const secret = await screen.findByLabelText(/Select example\/api/);

    expect(secret).toBeDisabled();
  });

  it('offers the stop switch only to a row that can reach production', async () => {
    stub();
    renderWithProviders(<Decisions />);

    await screen.findByText('express 4.16.0 → 4.21.2');

    expect(screen.getAllByLabelText(/Stop before production:/)).toHaveLength(1);
  });

  it('filters by application, type, goes up to and text', async () => {
    stub();
    renderWithProviders(<Decisions />);
    await screen.findByText('express 4.16.0 → 4.21.2');

    await userEvent.selectOptions(screen.getByLabelText('Application'), 'example/web-portal');
    expect(screen.queryByText('express 4.16.0 → 4.21.2')).not.toBeInTheDocument();
    expect(screen.getByText('react 17.0.2 → 18.3.1')).toBeInTheDocument();

    await userEvent.selectOptions(screen.getByLabelText('Application'), '');
    await userEvent.selectOptions(screen.getByLabelText('Plan'), 'noAction');
    expect(within(screen.getByRole('table')).getByText('Secret')).toBeInTheDocument();
    expect(screen.queryByText('react 17.0.2 → 18.3.1')).not.toBeInTheDocument();

    await userEvent.selectOptions(screen.getByLabelText('Plan'), '');
    await userEvent.selectOptions(screen.getByLabelText('Type'), 'exposedSecret');
    expect(screen.getAllByRole('row')).toHaveLength(2);

    await userEvent.selectOptions(screen.getByLabelText('Type'), '');
    await userEvent.type(screen.getByLabelText('Search a package or a text'), 'lodash');
    expect(screen.getByText('lodash 4.17.4 → 4.17.21')).toBeInTheDocument();
    expect(screen.queryByText('react 17.0.2 → 18.3.1')).not.toBeInTheDocument();
  });

  it('says there is no match when the filters leave nothing', async () => {
    stub();
    renderWithProviders(<Decisions />);
    await screen.findByText('express 4.16.0 → 4.21.2');

    await userEvent.type(screen.getByLabelText('Search a package or a text'), 'nothing like this');

    expect(screen.getByText('No problem matches these filters.')).toBeInTheDocument();
  });

  it('groups the selection by application, one batch each, and says what each batch does', async () => {
    stub();
    renderWithProviders(<Decisions />);
    await userEvent.click(await screen.findByLabelText(/Select example\/shop express/));
    await userEvent.click(screen.getByLabelText(/Select example\/web-portal react/));

    expect(screen.getByText('2 problems in 2 applications → 2 batches, one code')).toBeInTheDocument();
    expect(screen.getByText(/example\/shop: .*production allowed, not built yet/)).toBeInTheDocument();
    expect(screen.getByText(/example\/web-portal: .*merge into develop → stops before production/)).toBeInTheDocument();
    expect(screen.getByText('4 of 4 problems · 2 selected')).toBeInTheDocument();
  });

  it('says a row that stops before production no longer promotes', async () => {
    stub();
    renderWithProviders(<Decisions />);
    await userEvent.click(await screen.findByLabelText(/Select example\/shop express/));
    await userEvent.click(screen.getByLabelText(/Stop before production: example\/shop/));

    expect(screen.getByText(/example\/shop: .*merge into develop → stops before production/)).toBeInTheDocument();
  });

  it('shows a pull request that fails as a repair, says what it will do and sends the ceiling it saw', async () => {
    const failing = problem({
      id: 'pr:example/shop#9',
      description: 'express 4.16.0 → 5.3.0',
      updateClass: 'major',
      checks: 'failing',
      goesUpTo: 'repair',
      why: 'qualificationFailed',
      canStopBeforeProduction: false,
      pullRequest: 9,
      headSha: head('d'),
    });
    const fetchMock = stub(problemsReport([failing]), { requiresSecondFactor: false });
    renderWithProviders(<Decisions />);

    await userEvent.click(await screen.findByLabelText(/Select example\/shop express/));

    const line = within(screen.getByRole('table')).getAllByRole('row')[1];
    expect(line).toHaveTextContent(/1Validate2Repair.*Fails here/);
    expect(
      screen.getByText(/example\/shop: an agent tries to repair 1 in the workshop.*nothing is pushed/),
    ).toBeInTheDocument();
    expect(screen.queryByLabelText(/Try to repair/)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Validate the selection' }));
    await waitFor(() => expect(posted(fetchMock)).toBeDefined());
    expect(posted(fetchMock)).toMatchObject({ rows: [{ id: 'pr:example/shop#9', goesUpTo: 'repair' }] });
  });

  it('sorts by a column when its header is clicked, ascending then descending then back, and a line keeps its number', async () => {
    stub();
    renderWithProviders(<Decisions />);
    await screen.findByText('express 4.16.0 → 4.21.2');
    const order = () =>
      within(screen.getByRole('table'))
        .getAllByRole('row')
        .slice(1)
        .map((line) => line.textContent?.match(/^\d+/)?.[0]);
    expect(order()).toEqual(['1', '2', '3', '4']);

    await userEvent.click(screen.getByRole('button', { name: 'Sort by Plan' }));
    expect(order()).toEqual(['4', '2', '3', '1']);
    expect(screen.getByRole('columnheader', { name: /Plan/ })).toHaveAttribute('aria-sort', 'ascending');

    await userEvent.click(screen.getByRole('button', { name: 'Sort by Plan' }));
    expect(order()).toEqual(['1', '3', '2', '4']);
    expect(screen.getByRole('columnheader', { name: /Plan/ })).toHaveAttribute('aria-sort', 'descending');

    await userEvent.click(screen.getByRole('button', { name: 'Sort by Plan' }));
    expect(order()).toEqual(['1', '2', '3', '4']);
  });

  it('sorts by application so that the lines of one application come together, whatever the filters', async () => {
    stub();
    renderWithProviders(<Decisions />);
    await screen.findByText('express 4.16.0 → 4.21.2');

    await userEvent.click(screen.getByRole('button', { name: 'Sort by Application' }));

    const apps = within(screen.getByRole('table'))
      .getAllByRole('row')
      .slice(1)
      .map((line) => line.textContent);
    expect(apps[0]).toMatch(/api/);
    expect(apps.at(-1)).toMatch(/web-portal/);
  });

  it('shows what the workshop said of the commit and filters on it', async () => {
    const failed = problem({
      id: 'pr:example/shop#9',
      description: 'express 4.16.0 → 5.3.0',
      pullRequest: 9,
      headSha: head('d'),
      workshop: 'fails',
      goesUpTo: 'repair',
      why: 'qualificationFailed',
      canStopBeforeProduction: false,
    });
    const passed = problem({
      id: 'pr:example/shop#8',
      description: 'mocha 8 → 12',
      pullRequest: 8,
      headSha: head('e'),
      workshop: 'passes',
      goesUpTo: 'qualification',
      why: 'passesHereChecksFailing',
      canStopBeforeProduction: false,
    });
    stub(problemsReport([failed, passed]), { requiresSecondFactor: false });
    renderWithProviders(<Decisions />);
    await screen.findByText('express 4.16.0 → 5.3.0');
    const table = within(screen.getByRole('table'));
    expect(table.getByText('fails')).toBeInTheDocument();
    expect(table.getByText('passes')).toBeInTheDocument();

    await userEvent.selectOptions(screen.getByLabelText('Workshop'), 'fails');

    expect(screen.queryByText('mocha 8 → 12')).not.toBeInTheDocument();
    expect(screen.getByText('express 4.16.0 → 5.3.0')).toBeInTheDocument();
  });

  it('selects every problem shown that can be done, and none of the others', async () => {
    stub();
    renderWithProviders(<Decisions />);

    await userEvent.click(await screen.findByLabelText('Select every problem shown that can be done'));

    expect(screen.getByText(/3 problems in 2 applications/)).toBeInTheDocument();
    expect(screen.getByLabelText(/Select example\/api/)).not.toBeChecked();
  });

  it('validates the selection with one code, sends the commit that was shown and the switches, then clears it', async () => {
    const fetchMock = stub();
    renderWithProviders(<Decisions />);
    await userEvent.click(await screen.findByLabelText(/Select example\/shop express/));
    await userEvent.click(screen.getByLabelText(/Select example\/web-portal react/));
    await userEvent.click(screen.getByLabelText(/Stop before production: example\/shop/));
    const validate = screen.getByRole('button', { name: 'Validate the selection' });
    expect(validate).toBeDisabled();

    await userEvent.type(screen.getByLabelText('Authenticator code'), '123 456');
    await userEvent.click(validate);

    await waitFor(() => expect(posted(fetchMock)).toBeDefined());
    expect(posted(fetchMock)).toEqual({
      rows: [
        {
          id: 'pr:example/shop#2',
          headSha: head('a'),
          goesUpTo: 'production',
          stopBeforeProduction: true,
        },
        {
          id: 'pr:example/web-portal#4',
          headSha: head('c'),
          goesUpTo: 'mergeIntoDevelop',
          stopBeforeProduction: false,
        },
      ],
      secondFactorCode: '123 456',
      descriptionsVersion: 'abc123',
    });
    expect(await screen.findByText(/Approved: 2 pull requests in one batch/)).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText('Select problems to validate them together.')).toBeInTheDocument());
  });

  it('asks for no code when the option is off, and sends none', async () => {
    const fetchMock = stub(problemsReport(rows), { requiresSecondFactor: false });
    renderWithProviders(<Decisions />);
    await userEvent.click(await screen.findByLabelText(/Select example\/shop express/));

    expect(screen.queryByLabelText('Authenticator code')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Validate the selection' }));

    await waitFor(() => expect(posted(fetchMock)).toBeDefined());
    expect(posted(fetchMock)?.secondFactorCode).toBeUndefined();
  });

  it('says why when the server refuses, and keeps the selection', async () => {
    stub(
      problemsReport(rows),
      { requiresSecondFactor: false },
      new Response(JSON.stringify({ error: 'planChanged' }), { status: 409 }),
    );
    renderWithProviders(<Decisions />);
    await userEvent.click(await screen.findByLabelText(/Select example\/shop express/));

    await userEvent.click(screen.getByRole('button', { name: 'Validate the selection' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/changed since you looked at them/);
    expect(screen.getByLabelText(/Select example\/shop express/)).toBeChecked();
  });

  it('lets only an operator validate', async () => {
    stub(problemsReport(rows), { requiresSecondFactor: false, isOperator: false });
    renderWithProviders(<Decisions />);

    await userEvent.click(await screen.findByLabelText(/Select example\/shop express/));

    expect(screen.getByText('Only an operator can validate.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Validate the selection' })).toBeDisabled();
  });

  it('drops a selected row whose pull request moved to another commit', async () => {
    const moved = [{ ...rows[0], headSha: head('f') }, ...rows.slice(1)];
    let call = 0;
    const fetchMock = vi.fn((input: string) => {
      const path = String(input);
      if (path.startsWith('/api/v1/problems')) {
        call += 1;
        return Promise.resolve(
          new Response(JSON.stringify(problemsReport(call === 1 ? rows : moved)), { status: 200 }),
        );
      }
      return Promise.resolve(
        new Response(JSON.stringify(status({}, { requiresSecondFactor: false })), { status: 200 }),
      );
    });
    vi.stubGlobal('fetch', fetchMock);
    renderWithProviders(<Decisions />);
    await userEvent.click(await screen.findByLabelText(/Select example\/shop express/));
    expect(screen.getByLabelText(/Select example\/shop express/)).toBeChecked();

    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    await waitFor(() => expect(screen.getByLabelText(/Select example\/shop express/)).not.toBeChecked());
  });

  it('says the pull requests are being read, the source is missing, or nothing needs deciding', async () => {
    stub(problemsReport([], { sourceKind: 'none' }));
    const none = renderWithProviders(<Decisions />);
    expect(await screen.findByText(/No source of findings is configured/)).toBeInTheDocument();
    none.unmount();

    stub(problemsReport([]));
    renderWithProviders(<Decisions />);
    expect(await screen.findByText(/Nothing needs a decision/)).toBeInTheDocument();
  });

  it('warns when what is shown is the last read and the last read failed', async () => {
    stub(problemsReport(rows, { stale: true, error: 'unreachable' }));
    renderWithProviders(<Decisions />);

    expect(await screen.findByText(/Showing what was read at/)).toBeInTheDocument();
  });

  it('shows the error when the table cannot be read at all', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response('{}', { status: 500 }))),
    );
    renderWithProviders(<Decisions />);

    expect(await screen.findByRole('alert')).toHaveTextContent(/Cannot read the fleet/);
    expect(within(screen.getByRole('alert')).queryByText('undefined')).not.toBeInTheDocument();
  });
});
