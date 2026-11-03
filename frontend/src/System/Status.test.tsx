import { screen } from '@testing-library/react';

import { renderWithProviders, status, stubApi } from 'testUtils';

import Status from './Status';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Status', () => {
  it('shows the source, the time of the last read and the version', async () => {
    stubApi({ '/api/v1/system/status': status() });

    renderWithProviders(<Status />);

    expect(await screen.findByText('Sample data')).toBeInTheDocument();
    const lastRead = screen.getByText('Last read').nextElementSibling;
    expect(lastRead).toHaveTextContent(/\d{1,2}.\d{2}.\d{2}/);
    expect(lastRead).not.toHaveTextContent('never');
    expect(screen.getByText('0.0.0-dev')).toBeInTheDocument();
  });

  it('says never when nothing was read yet', async () => {
    stubApi({ '/api/v1/system/status': status({ readAt: null }) });

    renderWithProviders(<Status />);

    expect(await screen.findByText('never')).toBeInTheDocument();
  });

  it('words why the source cannot be read', async () => {
    stubApi({ '/api/v1/system/status': status({ kind: 'portarr', error: 'unauthorized' }) });

    renderWithProviders(<Status />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Cannot read the fleet: The source refused the credentials, or asks to sign in',
    );
  });
});
