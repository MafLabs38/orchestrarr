import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { problem, renderWithProviders, stubApi } from 'testUtils';
import type { CodeLocation } from 'typings/api';

import CodeInfo, { RowWarnings } from './CodeInfo';

const location: CodeLocation = {
  repository: 'example/infra',
  path: 'k8s/cache.yaml',
  line: 12,
  form: 'imageLine',
  image: { name: 'redis', tag: '7.2.3' },
  managed: null,
};

describe('CodeInfo', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows nothing for a row that is not written anywhere in the code of the scope', () => {
    const { container } = renderWithProviders(<CodeInfo row={problem()} sentence="redis 7.2.3 → 7.2.6" />);

    expect(container).toBeEmptyDOMElement();
  });

  it('says where the version is written, and that Flux writes it when it does', async () => {
    renderWithProviders(
      <CodeInfo
        row={problem({ locations: [location, { ...location, line: 30, managed: 'flux' }] })}
        sentence="redis 7.2.3 → 7.2.6"
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: /Where the version of .* is written/ }));

    expect(screen.getByText('example/infra/k8s/cache.yaml:12')).toBeInTheDocument();
    expect(screen.getByText(/rewritten by flux/)).toBeInTheDocument();
  });

  it('shows the change that would be written, as a diff, and says that nothing is written', async () => {
    stubApi({
      '/api/v1/problems/change?id=finding%3Aredis': {
        rowId: 'finding:redis',
        tag: '7.2.6',
        changes: [
          {
            repository: 'example/infra',
            path: 'k8s/cache.yaml',
            line: 12,
            before: 'image: redis:7.2.3',
            after: 'image: redis:7.2.6',
            diff: '-image: redis:7.2.3\n+image: redis:7.2.6\n',
          },
        ],
        left: [],
      },
    });
    renderWithProviders(
      <CodeInfo row={problem({ id: 'finding:redis', locations: [location], proposedTag: '7.2.6' })} sentence="redis" />,
    );

    await userEvent.click(screen.getByRole('button', { name: /Where the version of/ }));

    await waitFor(() => expect(screen.getByText(/\+image: redis:7.2.6/)).toBeInTheDocument());
    expect(screen.getByText('The change to write 7.2.6')).toBeInTheDocument();
    expect(screen.getByText('Nothing is written to the repository.')).toBeInTheDocument();
  });

  it('says on the row which repository cannot be read and why, and says nothing for a row that no unreadable repository concerns', async () => {
    const { rerender } = renderWithProviders(
      <RowWarnings
        row={problem({
          warnings: [
            { repository: 'example/infra', reason: 'no file matches its paths' },
            { repository: 'example/infra', reason: '2 files could not be read' },
          ],
        })}
        sentence="sonarqube"
      />,
    );

    await userEvent.click(screen.getByRole('button', { name: 'Warning about example/shop sonarqube' }));

    expect(screen.getByText('A repository of the description cannot be read')).toBeInTheDocument();
    expect(screen.getAllByText('example/infra')).toHaveLength(2);
    expect(screen.getByText(/no file matches its paths/)).toBeInTheDocument();
    expect(screen.getByText(/2 files could not be read/)).toBeInTheDocument();

    rerender(<RowWarnings row={problem({ warnings: null })} sentence="sonarqube" />);
    expect(screen.queryByRole('button', { name: /Warning about/ })).not.toBeInTheDocument();
  });
});
