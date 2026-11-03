import { render, screen } from '@testing-library/react';

import SignInFailed from './SignInFailed';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SignInFailed', () => {
  it('says signing in failed, and offers to try again, when the server sent the browser back for that', () => {
    vi.stubGlobal('location', { pathname: '/', search: '?signInFailed=1' });

    render(<SignInFailed />);

    expect(screen.getByRole('alert')).toHaveTextContent('Signing in failed');
    expect(screen.getByRole('link', { name: 'Sign in again' })).toHaveAttribute('href', '/auth/login');
  });

  it('says nothing otherwise', () => {
    vi.stubGlobal('location', { pathname: '/', search: '' });

    const { container } = render(<SignInFailed />);

    expect(container).toBeEmptyDOMElement();
  });
});
