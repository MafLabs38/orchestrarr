import { screen, waitFor } from '@testing-library/react';

import { renderWithProviders, status, stubApi, stubPendingApi } from 'testUtils';

import SourceBadge from './SourceBadge';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SourceBadge', () => {
  it.each([
    [status({ kind: 'portarr' }), 'Portarr · Connected'],
    [status({ kind: 'portarr', error: 'unreachable' }), 'Portarr · Cannot be read'],
    [status({ kind: 'sample' }), 'Sample data · Connected'],
    [status({ kind: 'none', configured: false }), 'None'],
    [status({ kind: 'something-new' }), 'something-new · Connected'],
  ])('says where the findings come from, and whether they can be read', async (answer, text) => {
    stubApi({ '/api/v1/system/status': answer });

    renderWithProviders(<SourceBadge />);

    expect(await screen.findByText(text)).toBeInTheDocument();
  });

  it('shows nothing while the status is being read', () => {
    stubPendingApi();

    const { container } = renderWithProviders(<SourceBadge />);

    expect(container).toBeEmptyDOMElement();
  });

  it('shows nothing when the status cannot be read', async () => {
    const fetchMock = stubApi({});

    const { container } = renderWithProviders(<SourceBadge />);

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    await waitFor(() => expect(container).toBeEmptyDOMElement());
  });
});
