import { render, screen } from '@testing-library/react';

import { finding, pullRequest, pullRequestsRead, workItem } from 'testUtils';
import type { Finding, PullRequestsRead, WorkItem } from 'typings/api';

import PullRequestList from './PullRequestList';

const waiting = { ...finding, kind: 'openDependencyPullRequests' as const, count: 3, alerts: null };

const item = (pullRequests: PullRequestsRead, findings: Finding[] = [waiting]): WorkItem => ({
  ...workItem,
  findings,
  pullRequests,
});

describe('PullRequestList', () => {
  it('lists the pull requests and says how many are ready to merge as they stand', () => {
    const three = [
      pullRequest(1),
      pullRequest(2, { checks: 'failing', readyToMerge: false }),
      pullRequest(3, { merge: 'conflicting', readyToMerge: false }),
    ];

    render(<PullRequestList item={item(pullRequestsRead({ items: three }))} />);

    expect(screen.getByRole('heading', { name: /Pull requests · 1 of 3 ready to merge/ })).toBeInTheDocument();
    expect(screen.getAllByRole('listitem')).toHaveLength(3);
    expect(screen.queryByText(/Portarr counts/)).not.toBeInTheDocument();
  });

  it('offers nothing to act on', () => {
    render(
      <PullRequestList item={item(pullRequestsRead({ items: [pullRequest(1), pullRequest(2), pullRequest(3)] }))} />,
    );

    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  });

  it('says no forge is connected instead of showing nothing', () => {
    render(<PullRequestList item={item(pullRequestsRead({ status: 'notConnected', items: [] }))} />);

    expect(screen.getByText('Pull requests were not read: no forge is connected.')).toBeInTheDocument();
  });

  it('says the pull requests are being read', () => {
    render(<PullRequestList item={item(pullRequestsRead({ status: 'pending', items: [] }))} />);

    expect(screen.getByText('Reading the pull requests…')).toBeInTheDocument();
  });

  it('words why reading them failed, with the code of the error', () => {
    render(<PullRequestList item={item(pullRequestsRead({ status: 'failed', items: [], error: 'rateLimited' }))} />);

    expect(
      screen.getByText('Pull requests could not be read: The source asks to be read less often: its limit is reached'),
    ).toBeInTheDocument();
  });

  it('keeps showing the last good list when a read fails, and says it is the last one', () => {
    render(
      <PullRequestList
        item={item(
          pullRequestsRead({
            status: 'failed',
            error: 'timeout',
            stale: true,
            items: [pullRequest(1), pullRequest(2), pullRequest(3)],
          }),
        )}
      />,
    );

    expect(screen.getByText('Pull requests could not be read: The source did not answer in time')).toBeInTheDocument();
    expect(screen.getByText('Showing the last list that was read.')).toBeInTheDocument();
    expect(screen.getAllByRole('listitem')).toHaveLength(3);
  });

  it('says so when the forge returned none of the pull requests Portarr counts, instead of showing nothing', () => {
    render(<PullRequestList item={item(pullRequestsRead({ items: [] }))} />);

    expect(screen.getByText(/The forge returned none of the 3 pull requests that Portarr counts/)).toBeInTheDocument();
    expect(screen.queryByRole('list')).not.toBeInTheDocument();
  });

  it('says so when the list is cut at the limit', () => {
    render(<PullRequestList item={item(pullRequestsRead({ items: [pullRequest(1)], truncated: true }))} />);

    expect(screen.getByText(/Showing 1 of the pull requests: there are more than the limit/)).toBeInTheDocument();
  });

  it('says so when the forge returned another number than Portarr counts', () => {
    render(<PullRequestList item={item(pullRequestsRead({ items: [pullRequest(1), pullRequest(2)] }))} />);

    expect(screen.getByText('Portarr counts 3 pull requests, the forge returned 2.')).toBeInTheDocument();
  });

  it.each([
    ['not needed', pullRequestsRead({ status: 'notNeeded', items: [] }), [waiting]],
    ['no pull request finding', pullRequestsRead(), [finding]],
  ])('shows nothing when %s', (_name, read, findings) => {
    const { container } = render(<PullRequestList item={item(read, findings)} />);

    expect(container).toBeEmptyDOMElement();
  });
});
