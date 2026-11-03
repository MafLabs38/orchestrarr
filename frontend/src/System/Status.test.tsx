import { screen } from '@testing-library/react';

import { renderWithProviders, status, stubApi } from 'testUtils';

import Status from './Status';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Status', () => {
  it('shows the source, the last read and the version', async () => {
    stubApi({ '/api/v1/system/status': status() });

    renderWithProviders(<Status />);

    expect(await screen.findByText('Sample data')).toBeInTheDocument();
    expect(screen.getByText('0.0.0-dev')).toBeInTheDocument();
  });

  it('shows why the source cannot be read', async () => {
    stubApi({ '/api/v1/system/status': status({ kind: 'portarr', error: '401 Unauthorized' }) });

    renderWithProviders(<Status />);

    expect(await screen.findByRole('alert')).toHaveTextContent('Cannot read the fleet: 401 Unauthorized');
  });
});
