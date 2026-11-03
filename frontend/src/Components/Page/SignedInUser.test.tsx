import { screen, waitFor } from '@testing-library/react';

import { renderWithProviders, status, stubApi } from 'testUtils';

import SignedInUser from './SignedInUser';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SignedInUser', () => {
  it('says who is signed in by the name to show, and signs out with a form', async () => {
    stubApi({
      '/api/v1/system/status': status({}, { mode: 'openidconnect', operator: 'a1b2', operatorName: 'alice' }),
    });

    renderWithProviders(<SignedInUser />);

    expect(await screen.findByText('Signed in as alice')).toBeInTheDocument();
    const button = screen.getByRole('button', { name: 'Sign out' });
    expect(button).toHaveAttribute('type', 'submit');
    const form = button.closest('form');
    expect(form).toHaveAttribute('method', 'post');
    expect(form).toHaveAttribute('action', '/auth/logout');
  });

  it('falls back on the identifier when there is no name', async () => {
    stubApi({
      '/api/v1/system/status': status({}, { mode: 'openidconnect', operator: 'a1b2', operatorName: null }),
    });

    renderWithProviders(<SignedInUser />);

    expect(await screen.findByText('Signed in as a1b2')).toBeInTheDocument();
  });

  it.each([
    [{ mode: 'development' }],
    [{ mode: 'disabled', enabled: false, operator: null, operatorName: null }],
    [{ mode: 'openidconnect', operator: null, operatorName: null }],
  ])('shows nothing where nobody has anything to sign out of', async (approvals) => {
    const fetchMock = stubApi({ '/api/v1/system/status': status({}, approvals) });

    const { container } = renderWithProviders(<SignedInUser />);

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    await waitFor(() => expect(container).toBeEmptyDOMElement());
  });
});
