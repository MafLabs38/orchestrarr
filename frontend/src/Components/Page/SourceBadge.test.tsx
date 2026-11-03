import { screen } from '@testing-library/react';

import { renderWithProviders, status, stubApi } from 'testUtils';

import SourceBadge from './SourceBadge';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SourceBadge', () => {
  it.each([
    [status({ kind: 'portarr' }), 'Portarr · Connected'],
    [status({ kind: 'portarr', error: 'refused' }), 'Portarr · Cannot be read'],
    [status({ kind: 'none', configured: false }), 'None · None'],
  ])('says where the findings come from, and whether they can be read', async (answer, text) => {
    stubApi({ '/api/v1/system/status': answer });

    renderWithProviders(<SourceBadge />);

    expect(await screen.findByText(text)).toBeInTheDocument();
  });

  it('shows nothing until the status is known', () => {
    stubApi({});

    const { container } = renderWithProviders(<SourceBadge />);

    expect(container).toBeEmptyDOMElement();
  });
});
