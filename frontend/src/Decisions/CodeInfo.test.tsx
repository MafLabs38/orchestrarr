import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { problem, renderWithProviders, stubApi } from 'testUtils';
import type { CodeLocation } from 'typings/api';

import CodeInfo from './CodeInfo';

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
});
