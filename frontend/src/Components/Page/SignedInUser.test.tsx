import { screen } from '@testing-library/react';

import { renderWithProviders, status, stubApi } from 'testUtils';

import SignedInUser from './SignedInUser';
import SourceBadge from './SourceBadge';

afterEach(() => {
  vi.unstubAllGlobals();
});

// The status is read for the badge: when it shows, the session has been asked for too, and had its chance to show
function renderBeside(session?: unknown) {
  stubApi({ '/api/v1/system/status': status(), ...(session === undefined ? {} : { '/auth/me': session }) });
  renderWithProviders(
    <>
      <SignedInUser />
      <SourceBadge />
    </>,
  );
  return screen.findByText('Sample data · Connected');
}

describe('SignedInUser', () => {
  it('says who is signed in, and signs out with a form', async () => {
    await renderBeside({ name: 'alice', canRead: true, isOperator: true });

    expect(await screen.findByText('Signed in as alice')).toBeInTheDocument();
    const button = screen.getByRole('button', { name: 'Sign out' });
    expect(button).toHaveAttribute('type', 'submit');
    const form = button.closest('form');
    expect(form).toHaveAttribute('method', 'post');
    expect(form).toHaveAttribute('action', '/auth/logout');
  });

  it('offers the way out to an account that the API refuses', async () => {
    await renderBeside({ name: 'carol', canRead: false, isOperator: false });

    expect(await screen.findByRole('button', { name: 'Sign out' })).toBeInTheDocument();
  });

  it('shows nothing where people do not sign in: the route is not there', async () => {
    await renderBeside();

    expect(screen.queryByRole('button', { name: 'Sign out' })).not.toBeInTheDocument();
  });
});
