import { render, screen } from '@testing-library/react';

import type { ChecksState, PullRequest } from 'typings/api';

import PullRequestRow from './PullRequestRow';

const pullRequest: PullRequest = {
  number: 12,
  title: 'chore(deps): Bump minimist from 0.0.8 to 1.2.8',
  url: 'https://forge.example/example/app/pull/12',
  author: 'dependabot[bot]',
  headBranch: 'bot/minimist',
  draft: false,
  checks: 'passing',
  merge: 'clean',
  labels: [],
  changes: [{ package: 'minimist', from: '0.0.8', to: '1.2.8', kind: 'major' }],
  createdAt: '2026-11-01T09:38:16Z',
};

const renderRow = (overrides: Partial<PullRequest> = {}) =>
  render(
    <ul>
      <PullRequestRow pullRequest={{ ...pullRequest, ...overrides }} />
    </ul>,
  );

describe('PullRequestRow', () => {
  it('links to the pull request and says what it updates and how far, in words', () => {
    renderRow();

    expect(screen.getByRole('link', { name: 'Pull request #12' })).toHaveAttribute(
      'href',
      'https://forge.example/example/app/pull/12',
    );
    expect(screen.getByText('minimist 0.0.8 → 1.2.8')).toBeInTheDocument();
    expect(screen.getByText('major update')).toBeInTheDocument();
  });

  it('opens the link safely', () => {
    renderRow();

    const link = screen.getByRole('link', { name: 'Pull request #12' });
    expect(link).toHaveAttribute('target', '_blank');
    expect(link.getAttribute('rel')).toContain('noopener');
  });

  it.each(['javascript:alert(1)', 'http://forge.example/pr/1', 'not a url', 'data:text/html,x'])(
    'does not make a link of %s',
    (url) => {
      renderRow({ url });

      expect(screen.queryByRole('link')).not.toBeInTheDocument();
      expect(screen.getByText('#12')).toBeInTheDocument();
    },
  );

  it.each<[ChecksState, string]>([
    ['passing', 'Checks pass'],
    ['failing', 'Checks fail'],
    ['pending', 'Checks running'],
    ['unknown', 'No check'],
  ])('says the checks %s in words', (checks, text) => {
    renderRow({ checks });

    expect(screen.getByText(text)).toBeInTheDocument();
  });

  it('says whether it can be merged', () => {
    renderRow({ merge: 'conflicting' });

    expect(screen.getByText('Conflicts with its base branch')).toBeInTheDocument();
  });

  it('says a draft is a draft whatever the merge state', () => {
    renderRow({ draft: true, merge: 'clean' });

    expect(screen.getByText('Draft')).toBeInTheDocument();
    expect(screen.queryByText('Can be merged')).not.toBeInTheDocument();
  });

  it('names several packages without a versions or a kind it does not know', () => {
    renderRow({
      title: 'chore(deps): Bump qs and express',
      changes: [
        { package: 'qs', from: null, to: null, kind: 'unknown' },
        { package: 'express', from: null, to: null, kind: 'unknown' },
      ],
    });

    expect(screen.getByText('qs, express')).toBeInTheDocument();
    expect(screen.queryByText(/update$/)).not.toBeInTheDocument();
  });

  it('falls back to the title when it understands nothing of it', () => {
    renderRow({ title: 'Something else', changes: [] });

    expect(screen.getByText('Something else')).toBeInTheDocument();
  });

  it('shows the biggest jump of the pull request', () => {
    renderRow({
      changes: [
        { package: 'a', from: '1.0.0', to: '1.0.1', kind: 'patch' },
        { package: 'b', from: '1.0.0', to: '2.0.0', kind: 'major' },
      ],
    });

    expect(screen.getByText('major update')).toBeInTheDocument();
  });
});
