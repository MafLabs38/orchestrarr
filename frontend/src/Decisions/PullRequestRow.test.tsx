import { render, screen } from '@testing-library/react';

import { pullRequest } from 'testUtils';
import type { ChecksState, PullRequest } from 'typings/api';

import PullRequestRow from './PullRequestRow';

const base = pullRequest(12, {
  title: 'chore(deps): Bump minimist from 0.0.8 to 1.2.8',
  url: 'https://forge.example/example/app/pull/12',
  changes: [{ package: 'minimist', from: '0.0.8', to: '1.2.8', kind: 'major' }],
});

const renderRow = (overrides: Partial<PullRequest> = {}) =>
  render(
    <ul>
      <PullRequestRow pullRequest={{ ...base, ...overrides }} />
    </ul>,
  );

describe('PullRequestRow', () => {
  it('links to the pull request and says what it updates and how far, in words, with its commit', () => {
    renderRow();

    expect(screen.getByRole('link', { name: 'Pull request #12' })).toHaveAttribute(
      'href',
      'https://forge.example/example/app/pull/12',
    );
    expect(screen.getByText('minimist 0.0.8 → 1.2.8')).toBeInTheDocument();
    expect(screen.getByText('major update')).toBeInTheDocument();
    expect(screen.getByText('abcdef0')).toBeInTheDocument();
  });

  it('opens the link safely', () => {
    renderRow();

    const link = screen.getByRole('link', { name: 'Pull request #12' });
    expect(link).toHaveAttribute('target', '_blank');
    expect(link.getAttribute('rel')).toContain('noopener');
  });

  it.each(['javascript:alert(1)', 'http://forge.example/pr/1', 'not a url'])('does not make a link of %s', (url) => {
    renderRow({ url });

    expect(screen.queryByRole('link')).not.toBeInTheDocument();
    expect(screen.getByText('#12')).toBeInTheDocument();
  });

  it('offers nothing to act on: it only shows', () => {
    renderRow();

    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
    expect(document.querySelector('form')).toBeNull();
  });

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

  it('names several packages without versions or a kind it does not know', () => {
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

  it('falls back to the title when it understands nothing of it, and shows no commit it does not have', () => {
    renderRow({ title: 'Something else', changes: [], headSha: '' });

    expect(screen.getByText('Something else')).toBeInTheDocument();
    expect(screen.queryByText(/^[0-9a-f]{7}$/)).not.toBeInTheDocument();
  });
});
