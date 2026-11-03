import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { finding, step, workItem } from 'testUtils';
import type { Severity, WorkItem } from 'typings/api';

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
  it('shows the title, the severity in words and the findings in words', () => {
    render(<DecisionCard item={item} />);

    expect(screen.getByRole('heading', { name: 'example/shop' })).toBeInTheDocument();
    expect(screen.getByText('Urgent')).toBeInTheDocument();
    expect(screen.getByText('4 dependency alerts (1 critical, 2 high, 1 medium)')).toBeInTheDocument();
    expect(screen.getByText('5 open dependency pull requests waiting')).toBeInTheDocument();
    expect(screen.getByText('Apps: shop')).toBeInTheDocument();
  });

  it.each<[Severity, string]>([
    ['danger', 'Urgent'],
    ['warning', 'Attention'],
    ['info', 'Routine'],
  ])('says a %s work item in words', (severity, text) => {
    render(<DecisionCard item={{ ...item, severity }} />);

    expect(screen.getByText(text)).toBeInTheDocument();
  });

  it('ticks every available step by default', () => {
    render(<DecisionCard item={item} />);

    const boxes = screen.getAllByRole('checkbox');
    expect(boxes).toHaveLength(3);
    boxes.forEach((box) => expect(box).toBeChecked());
    expect(screen.getByText('3 of 3 steps ticked')).toBeInTheDocument();
  });

  it('lets the operator untick a step and says how many remain', async () => {
    render(<DecisionCard item={item} />);

    await userEvent.click(screen.getByRole('checkbox', { name: 'Deploy to Cluster' }));

    expect(screen.getByRole('checkbox', { name: 'Deploy to Cluster' })).not.toBeChecked();
    expect(screen.getByText('2 of 3 steps ticked')).toBeInTheDocument();
  });

  it('disables a step that cannot be done, leaves it unticked and says why', () => {
    render(<DecisionCard item={noPlacement} />);

    expect(deployBox()).toBeDisabled();
    expect(deployBox()).not.toBeChecked();
    expect(
      screen.getByText('Not possible: The environment description does not say where this runs.'),
    ).toBeInTheDocument();
    expect(screen.getByText('2 of 3 steps ticked')).toBeInTheDocument();
  });

  it('never shows an unavailable step as ticked, whatever the server says', () => {
    render(
      <DecisionCard
        item={{ ...item, plan: [step({ kind: 'deploy', ticked: true, available: false, reason: 'noPlacement' })] }}
      />,
    );

    expect(deployBox()).not.toBeChecked();
    expect(screen.getByText('0 of 1 steps ticked')).toBeInTheDocument();
  });

  it('leaves an available step the server did not tick unticked, and lets the operator tick it', async () => {
    render(<DecisionCard item={{ ...item, plan: [step({ kind: 'deploy', ticked: false })] }} />);

    expect(deployBox()).toBeEnabled();
    expect(deployBox()).not.toBeChecked();
    await userEvent.click(deployBox());
    expect(deployBox()).toBeChecked();
  });

  it('ignores a click on a step that cannot be done', async () => {
    render(<DecisionCard item={noPlacement} />);

    await userEvent.click(deployBox());

    expect(deployBox()).not.toBeChecked();
    expect(screen.getByText('2 of 3 steps ticked')).toBeInTheDocument();
  });

  it('keeps the choices of the operator when the plan changes under the card', async () => {
    const { rerender } = render(<DecisionCard item={item} />);
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
    render(<DecisionCard item={{ ...item, plan: [] }} />);

    expect(screen.getByText('Nothing can be done automatically here: a person has to act.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve and run' })).not.toBeInTheDocument();
  });

  it('shows the pull requests the work item waits for, inside the card', () => {
    const pullRequest = {
      number: 12,
      title: 'Bump a from 1.0.0 to 2.0.0',
      url: 'https://forge.example/o/r/pull/12',
      author: 'bot',
      headBranch: 'bot/a',
      draft: false,
      checks: 'failing' as const,
      merge: 'unstable' as const,
      labels: [],
      changes: [{ package: 'a', from: '1.0.0', to: '2.0.0', kind: 'major' as const }],
      createdAt: '2026-11-01T09:38:16Z',
    };

    render(<DecisionCard item={{ ...item, pullRequests: [pullRequest] }} />);

    expect(screen.getByText('a 1.0.0 → 2.0.0')).toBeInTheDocument();
    expect(screen.getByText('Checks fail')).toBeInTheDocument();
    expect(screen.getByText(/0 of 1 ready to merge/)).toBeInTheDocument();
  });

  it('cannot be approved yet, and says so next to the button', () => {
    render(<DecisionCard item={item} />);

    const button = screen.getByRole('button', { name: 'Approve and run' });
    expect(button).toBeDisabled();
    expect(button).toHaveAccessibleDescription(/no power is granted/);
  });
});
