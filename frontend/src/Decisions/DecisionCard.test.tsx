import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import {
  finding,
  pullRequest,
  pullRequestsRead,
  renderWithProviders,
  status,
  step,
  stubApi,
  workItem,
} from 'testUtils';
import type { ApprovalStatus, WorkItem } from 'typings/api';

import DecisionCard from './DecisionCard';

const item: WorkItem = {
  ...workItem,
  findings: [
    { ...finding, count: 4, alerts: { critical: 1, high: 2, medium: 1, low: 0 } },
    { kind: 'openDependencyPullRequests', severity: 'info', count: 5, alerts: null, image: null },
  ],
  plan: [
    step({ kind: 'validate' }),
    step({ kind: 'mergePullRequests' }),
    step({ kind: 'deploy', environmentId: 'cluster', environmentName: 'Cluster' }),
  ],
};

const noPlacement: WorkItem = {
  ...item,
  plan: [...item.plan.slice(0, 2), step({ kind: 'deploy', ticked: false, available: false, reason: 'noPlacement' })],
};

const deployBox = () => screen.getByRole('checkbox', { name: /Deploy/ });

describe('DecisionCard', () => {
  beforeEach(() => {
    stubApi({});
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('says the findings in words, and the applications it serves', () => {
    renderWithProviders(<DecisionCard item={item} />);

    expect(screen.getByText('4 dependency alerts (1 critical, 2 high, 1 medium)')).toBeInTheDocument();
    expect(screen.getByText('5 open dependency pull requests waiting')).toBeInTheDocument();
    expect(screen.getByText('Apps: shop')).toBeInTheDocument();
  });

  it('is a region named after the application', () => {
    renderWithProviders(<DecisionCard item={item} />);

    expect(screen.getByRole('region', { name: 'example/shop' })).toBeInTheDocument();
  });

  it('ticks every available step by default', () => {
    renderWithProviders(<DecisionCard item={item} />);

    const boxes = screen.getAllByRole('checkbox');
    expect(boxes).toHaveLength(3);
    boxes.forEach((box) => expect(box).toBeChecked());
    expect(screen.getByText('3 of 3 steps ticked')).toBeInTheDocument();
  });

  it('lets the operator untick a step and says how many remain', async () => {
    renderWithProviders(<DecisionCard item={item} />);

    await userEvent.click(screen.getByRole('checkbox', { name: 'Deploy to Cluster' }));

    expect(screen.getByRole('checkbox', { name: 'Deploy to Cluster' })).not.toBeChecked();
    expect(screen.getByText('2 of 3 steps ticked')).toBeInTheDocument();
  });

  it('disables a step that cannot be done, leaves it unticked and says why', () => {
    renderWithProviders(<DecisionCard item={noPlacement} />);

    expect(deployBox()).toBeDisabled();
    expect(deployBox()).not.toBeChecked();
    expect(
      screen.getByText('Not possible: The environment description does not say where this runs.'),
    ).toBeInTheDocument();
    expect(screen.getByText('2 of 3 steps ticked')).toBeInTheDocument();
  });

  it('never shows an unavailable step as ticked, whatever the server says', () => {
    renderWithProviders(
      <DecisionCard
        item={{ ...item, plan: [step({ kind: 'deploy', ticked: true, available: false, reason: 'noPlacement' })] }}
      />,
    );

    expect(deployBox()).not.toBeChecked();
    expect(screen.getByText('0 of 1 steps ticked')).toBeInTheDocument();
  });

  it('leaves an available step the server did not tick unticked, and lets the operator tick it', async () => {
    renderWithProviders(<DecisionCard item={{ ...item, plan: [step({ kind: 'deploy', ticked: false })] }} />);

    expect(deployBox()).toBeEnabled();
    expect(deployBox()).not.toBeChecked();
    await userEvent.click(deployBox());
    expect(deployBox()).toBeChecked();
  });

  it('ignores a click on a step that cannot be done', async () => {
    renderWithProviders(<DecisionCard item={noPlacement} />);

    await userEvent.click(deployBox());

    expect(deployBox()).not.toBeChecked();
    expect(screen.getByText('2 of 3 steps ticked')).toBeInTheDocument();
  });

  it('keeps the choices of the operator when the plan changes under the card', async () => {
    const { rerender } = renderWithProviders(<DecisionCard item={item} />);
    await userEvent.click(screen.getByRole('checkbox', { name: 'Deploy to Cluster' }));

    const next: WorkItem = {
      ...item,
      plan: [item.plan[0], step({ kind: 'deploy', environmentId: 'b', environmentName: 'Server B' }), item.plan[2]],
    };
    rerender(<DecisionCard item={next} />);

    expect(screen.getByRole('checkbox', { name: 'Deploy to Cluster' })).not.toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Deploy to Server B' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: 'Build and test the change' })).toBeChecked();
  });

  it('says nothing can be done automatically when the plan is empty, with nothing to approve', () => {
    renderWithProviders(<DecisionCard item={{ ...item, plan: [] }} />);

    expect(screen.getByText('Nothing can be done automatically here: a person has to act.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve and run' })).not.toBeInTheDocument();
  });

  it('shows the pull requests the work item waits for, inside the card', () => {
    const failing = pullRequest(12, {
      checks: 'failing',
      merge: 'unstable',
      readyToMerge: false,
      changes: [{ package: 'a', from: '1.0.0', to: '2.0.0', kind: 'major' }],
    });

    renderWithProviders(<DecisionCard item={{ ...item, pullRequests: pullRequestsRead({ items: [failing] }) }} />);

    expect(screen.getByText('a 1.0.0 → 2.0.0')).toBeInTheDocument();
    expect(screen.getByText('Checks fail')).toBeInTheDocument();
    expect(screen.getByText(/0 of 1 ready to merge/)).toBeInTheDocument();
  });
});

const statusPath = '/api/v1/system/status';
const approvalsPath = '/api/v1/approvals';

// The steps the operator granted: building and testing a pull request, and merging the ones that pass.
const granted: WorkItem = {
  ...item,
  pullRequests: pullRequestsRead({
    items: [pullRequest(1), pullRequest(2, { headSha: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' })],
  }),
  plan: [
    step({ kind: 'validate', granted: true }),
    step({ kind: 'mergePullRequests', granted: true }),
    step({ kind: 'deploy', environmentId: 'cluster', environmentName: 'Cluster', granted: true }),
  ],
};

const approved = { approvals: [{ id: 'a' }] };

function stubApproving(
  approvals: Partial<ApprovalStatus> = {},
  answer: { status: number; body: unknown } = { status: 201, body: approved },
) {
  const fetchMock = vi.fn((input: string, init?: RequestInit) => {
    if (input === statusPath) {
      return Promise.resolve(new Response(JSON.stringify(status({}, approvals)), { status: 200 }));
    }
    if (input === approvalsPath && init?.method === 'POST') {
      return Promise.resolve(new Response(JSON.stringify(answer.body), { status: answer.status }));
    }
    return Promise.resolve(new Response('{}', { status: 404 }));
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

const approveButton = () => screen.getByRole('button', { name: 'Approve' });
const posted = (fetchMock: ReturnType<typeof stubApproving>) =>
  fetchMock.mock.calls
    .filter(([, init]) => init?.method === 'POST')
    .map(([, init]) => JSON.parse(String(init?.body)) as unknown);

describe('DecisionCard approval', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('cannot be approved while approving is turned off, and says so next to the button', async () => {
    stubApproving({ enabled: false, mode: 'disabled', operator: null, operatorName: null, isOperator: false });

    renderWithProviders(<DecisionCard item={granted} />);

    await waitFor(() => expect(approveButton()).toHaveAccessibleDescription(/turned off/));
    expect(approveButton()).toBeDisabled();
  });

  it('cannot be approved by somebody who is signed in but is not an operator, and says so', async () => {
    stubApproving({ mode: 'openidconnect', operator: 'bob-2', operatorName: 'bob', isOperator: false });

    renderWithProviders(<DecisionCard item={granted} />);

    await waitFor(() => expect(approveButton()).toHaveAccessibleDescription(/not an operator/));
    expect(approveButton()).toBeDisabled();
  });

  it('cannot be approved while the global stop is engaged', async () => {
    stubApproving({ killSwitchEngaged: true });

    renderWithProviders(<DecisionCard item={granted} />);

    await waitFor(() => expect(approveButton()).toHaveAccessibleDescription(/global stop is engaged/));
    expect(approveButton()).toBeDisabled();
  });

  it('cannot be approved when no step is granted, and marks the steps that are not', async () => {
    stubApproving();

    renderWithProviders(<DecisionCard item={item} />);

    await waitFor(() => expect(approveButton()).toHaveAccessibleDescription(/No step of this plan is granted/));
    expect(approveButton()).toBeDisabled();
    expect(screen.getAllByText('Not granted')).toHaveLength(3);
  });

  it('marks a granted step that no approval runs yet', async () => {
    stubApproving();

    renderWithProviders(<DecisionCard item={granted} />);

    expect(await screen.findByText('Runs with a later version')).toBeInTheDocument();
  });

  it('says what approving does when it can be done', async () => {
    stubApproving();

    renderWithProviders(<DecisionCard item={granted} />);

    await waitFor(() => expect(approveButton()).toBeEnabled());
    expect(approveButton()).toHaveAccessibleDescription(/Nothing runs yet/);
  });

  it('sends only the work item and the steps that are ticked, granted and recorded today', async () => {
    const fetchMock = stubApproving();
    renderWithProviders(<DecisionCard item={granted} />);
    await waitFor(() => expect(approveButton()).toBeEnabled());

    await userEvent.click(approveButton());

    await waitFor(() =>
      expect(posted(fetchMock)).toEqual([
        {
          workItemId: 'repository:example/shop',
          steps: ['validate', 'mergePullRequests'],
          pullRequests: [
            { number: 1, headSha: 'abcdef0123456789abcdef0123456789abcdef01' },
            { number: 2, headSha: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' },
          ],
        },
      ]),
    );
  });

  it('sends one step less when the operator unticks one', async () => {
    const fetchMock = stubApproving();
    renderWithProviders(<DecisionCard item={granted} />);
    await waitFor(() => expect(approveButton()).toBeEnabled());

    await userEvent.click(screen.getByRole('checkbox', { name: 'Merge the pull requests that pass' }));
    await userEvent.click(approveButton());

    await waitFor(() =>
      expect(posted(fetchMock)).toEqual([
        expect.objectContaining({ workItemId: 'repository:example/shop', steps: ['validate'] }),
      ]),
    );
  });

  it('cannot be approved once every recorded step is unticked', async () => {
    stubApproving();
    renderWithProviders(<DecisionCard item={granted} />);
    await waitFor(() => expect(approveButton()).toBeEnabled());

    await userEvent.click(screen.getByRole('checkbox', { name: 'Build and test the change' }));
    await userEvent.click(screen.getByRole('checkbox', { name: 'Merge the pull requests that pass' }));

    expect(approveButton()).toBeDisabled();
  });

  it('says it was recorded, and links to the queue', async () => {
    stubApproving();
    renderWithProviders(<DecisionCard item={granted} />);
    await waitFor(() => expect(approveButton()).toBeEnabled());

    await userEvent.click(approveButton());

    expect(await screen.findByText(/1 approval recorded/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'See the queue' })).toHaveAttribute('href', '/queue');
  });

  it('counts several approvals', async () => {
    stubApproving({}, { status: 201, body: { approvals: [{ id: 'a' }, { id: 'b' }] } });
    renderWithProviders(<DecisionCard item={granted} />);
    await waitFor(() => expect(approveButton()).toBeEnabled());

    await userEvent.click(approveButton());

    expect(await screen.findByText(/2 approvals recorded/)).toBeInTheDocument();
  });

  it('says in words why the server refused', async () => {
    stubApproving({}, { status: 409, body: { error: 'alreadyApproved' } });
    renderWithProviders(<DecisionCard item={granted} />);
    await waitFor(() => expect(approveButton()).toBeEnabled());

    await userEvent.click(approveButton());

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'These pull requests were already approved at this commit.',
    );
  });

  it('still says something when the server refuses with a code it does not know', async () => {
    stubApproving({}, { status: 500, body: {} });
    renderWithProviders(<DecisionCard item={granted} />);
    await waitFor(() => expect(approveButton()).toBeEnabled());

    await userEvent.click(approveButton());

    expect(await screen.findByRole('alert')).toHaveTextContent('The approval was refused.');
  });

  it('sends only the pull requests it displays: a draft is not approved', async () => {
    const withDraft: WorkItem = {
      ...granted,
      pullRequests: pullRequestsRead({ items: [pullRequest(1), pullRequest(2, { draft: true })] }),
    };
    const fetchMock = stubApproving();
    renderWithProviders(<DecisionCard item={withDraft} />);
    await waitFor(() => expect(approveButton()).toBeEnabled());

    await userEvent.click(approveButton());

    await waitFor(() =>
      expect(posted(fetchMock)).toEqual([
        expect.objectContaining({ pullRequests: [{ number: 1, headSha: 'abcdef0123456789abcdef0123456789abcdef01' }] }),
      ]),
    );
  });

  it.each([
    ['still being read', pullRequestsRead({ status: 'pending', items: [] })],
    ['an old list', pullRequestsRead({ status: 'failed', items: [pullRequest(1)], stale: true, error: 'unreachable' })],
    ['none', pullRequestsRead({ status: 'read', items: [] })],
    ['not connected', pullRequestsRead({ status: 'notConnected', items: [] })],
  ])('cannot be approved on a list of pull requests that is %s', async (_name, pullRequests) => {
    stubApproving();
    renderWithProviders(<DecisionCard item={{ ...granted, pullRequests }} />);

    await waitFor(() => expect(approveButton()).toHaveAccessibleDescription(/not read yet/));
    expect(approveButton()).toBeDisabled();
  });

  it('posts once when the button is pressed twice while the answer is awaited', async () => {
    const awaited: { answer?: (response: Response) => void } = {};
    const fetchMock = vi.fn((input: string, init?: RequestInit) => {
      if (input === statusPath) {
        return Promise.resolve(new Response(JSON.stringify(status()), { status: 200 }));
      }
      if (input === approvalsPath && init?.method === 'POST') {
        return new Promise<Response>((resolve) => {
          awaited.answer = resolve;
        });
      }
      return Promise.resolve(new Response('[]', { status: 200 }));
    });
    vi.stubGlobal('fetch', fetchMock);
    renderWithProviders(<DecisionCard item={granted} />);
    await waitFor(() => expect(approveButton()).toBeEnabled());

    await userEvent.click(approveButton());
    await userEvent.click(approveButton());

    expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST')).toHaveLength(1);
    awaited.answer?.(new Response(JSON.stringify(approved), { status: 201 }));
    expect(await screen.findByText(/1 approval recorded/)).toBeInTheDocument();
  });

  it('reads the queue, the trail and the status again once something is recorded', async () => {
    const fetchMock = stubApproving();
    renderWithProviders(<DecisionCard item={granted} />);
    await waitFor(() => expect(approveButton()).toBeEnabled());
    const before = fetchMock.mock.calls.filter(([input]) => input === statusPath).length;

    await userEvent.click(approveButton());

    await waitFor(() =>
      expect(fetchMock.mock.calls.filter(([input]) => input === statusPath).length).toBeGreaterThan(before),
    );
  });

  it('forgets that it was recorded as soon as a step is ticked or unticked', async () => {
    stubApproving();
    renderWithProviders(<DecisionCard item={granted} />);
    await waitFor(() => expect(approveButton()).toBeEnabled());
    await userEvent.click(approveButton());
    expect(await screen.findByText(/1 approval recorded/)).toBeInTheDocument();

    await userEvent.click(screen.getByRole('checkbox', { name: 'Merge the pull requests that pass' }));

    expect(screen.queryByText(/approval recorded/)).not.toBeInTheDocument();
  });

  it('forgets that it was recorded when the pull requests, or their commits, change under the card', async () => {
    stubApproving();
    const { rerender } = renderWithProviders(<DecisionCard item={granted} />);
    await waitFor(() => expect(approveButton()).toBeEnabled());
    await userEvent.click(approveButton());
    expect(await screen.findByText(/1 approval recorded/)).toBeInTheDocument();

    rerender(
      <DecisionCard
        item={{ ...granted, pullRequests: pullRequestsRead({ items: [pullRequest(1, { headSha: 'f'.repeat(40) })] }) }}
      />,
    );

    await waitFor(() => expect(screen.queryByText(/approval recorded/)).not.toBeInTheDocument());
  });

  it('says in words that the pull requests changed', async () => {
    stubApproving({}, { status: 409, body: { error: 'planChanged' } });
    renderWithProviders(<DecisionCard item={granted} />);
    await waitFor(() => expect(approveButton()).toBeEnabled());

    await userEvent.click(approveButton());

    expect(await screen.findByRole('alert')).toHaveTextContent('The pull requests changed since you looked at them');
  });
});
