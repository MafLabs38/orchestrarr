import { render, screen } from '@testing-library/react';

import { finding, workItem } from 'testUtils';
import type { PullRequest, WorkItem } from 'typings/api';

import PullRequestList from './PullRequestList';

const waiting = { ...finding, kind: 'openDependencyPullRequests' as const, count: 3, alerts: null };

const pullRequest = (number: number, overrides: Partial<PullRequest> = {}): PullRequest => ({
  number,
  title: `Bump a from 1.0.0 to 1.0.${number}`,
  url: `https://forge.example/o/r/pull/${number}`,
  author: 'bot',
  headBranch: 'bot/a',
  draft: false,
  checks: 'passing',
  merge: 'clean',
  labels: [],
  changes: [{ package: 'a', from: '1.0.0', to: `1.0.${number}`, kind: 'patch' }],
  createdAt: '2026-11-01T09:38:16Z',
  ...overrides,
});

const item = (overrides: Partial<WorkItem>): WorkItem => ({ ...workItem, findings: [waiting], ...overrides });

describe('PullRequestList', () => {
  it('lists the pull requests and says how many are ready to merge as they stand', () => {
    const list = [pullRequest(1), pullRequest(2, { checks: 'failing' }), pullRequest(3, { merge: 'conflicting' })];

    render(<PullRequestList item={item({ pullRequests: list })} />);

    expect(screen.getByRole('heading', { name: /Pull requests · 1 of 3 ready to merge/ })).toBeInTheDocument();
    expect(screen.getAllByRole('listitem')).toHaveLength(3);
  });

  it('says the pull requests were not read when no forge is connected, instead of showing nothing', () => {
    render(<PullRequestList item={item({ pullRequests: null })} />);

    expect(screen.getByText('Pull requests were not read: no forge is connected.')).toBeInTheDocument();
  });

  it('words why reading them failed', () => {
    render(<PullRequestList item={item({ pullRequests: null, pullRequestsError: 'unauthorized' })} />);

    expect(
      screen.getByText('Pull requests could not be read: The source refused the credentials, or asks to sign in'),
    ).toBeInTheDocument();
  });

  it('shows nothing when the forge has none of a bot to show', () => {
    const { container } = render(<PullRequestList item={item({ pullRequests: [] })} />);

    expect(container).toBeEmptyDOMElement();
  });

  it('shows nothing for a work item that is not waiting for pull requests', () => {
    const { container } = render(<PullRequestList item={item({ findings: [finding], pullRequests: null })} />);

    expect(container).toBeEmptyDOMElement();
  });
});
