import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { WorkItem } from 'typings/api';
import DecisionCard from './DecisionCard';

const item: WorkItem = {
  id: 'repository:example/shop',
  title: 'example/shop',
  repositoryFullName: 'example/shop',
  appIds: ['shop'],
  severity: 'danger',
  findings: [
    { kind: 'dependencyAlerts', severity: 'danger', count: 4, summary: '4 dependency alerts (1 critical, 2 high, 1 medium)' },
    { kind: 'openDependencyPullRequests', severity: 'info', count: 5, summary: '5 open dependency pull requests waiting' },
  ],
  plan: [
    { kind: 'validate', label: 'Build and test the change', environmentId: null, ticked: true, available: true, reason: null },
    { kind: 'merge', label: 'Merge the pull requests that pass', environmentId: null, ticked: true, available: true, reason: null },
    { kind: 'deploy', label: 'Deploy to Cluster', environmentId: 'cluster', ticked: true, available: true, reason: null },
  ],
  placements: [],
};

const noPlacement: WorkItem = {
  ...item,
  plan: [
    ...item.plan.slice(0, 2),
    { kind: 'deploy', label: 'Deploy', environmentId: null, ticked: false, available: false, reason: 'The environment description does not say where this runs.' },
  ],
};

describe('DecisionCard', () => {
  it('shows the title, the severity in words and the findings', () => {
    render(<DecisionCard item={item} />);

    expect(screen.getByRole('heading', { name: 'example/shop' })).toBeInTheDocument();
    expect(screen.getByText('Urgent')).toBeInTheDocument();
    expect(screen.getByText('4 dependency alerts (1 critical, 2 high, 1 medium)')).toBeInTheDocument();
    expect(screen.getByText('Apps: shop')).toBeInTheDocument();
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

    const deploy = screen.getByRole('checkbox', { name: /Deploy/ });
    expect(deploy).toBeDisabled();
    expect(deploy).not.toBeChecked();
    expect(screen.getByText('Not possible: The environment description does not say where this runs.')).toBeInTheDocument();
    expect(screen.getByText('2 of 3 steps ticked')).toBeInTheDocument();
  });

  it('cannot be approved yet, and says so next to the button', () => {
    render(<DecisionCard item={item} />);

    const button = screen.getByRole('button', { name: 'Approve and run' });
    expect(button).toBeDisabled();
    expect(button).toHaveAccessibleDescription(/not available yet/);
  });
});
