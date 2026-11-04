import { render } from '@testing-library/react';

import BrandLogo from './BrandLogo';

describe('BrandLogo', () => {
  it('shows the logo at the height asked for, and leaves the name to the text beside it', () => {
    const { container } = render(<BrandLogo size={40} />);

    const image = container.querySelector('img');
    expect(image).toHaveAttribute('src', '/logo.png');
    expect(image).toHaveAttribute('height', '40');
    expect(image).toHaveAttribute('alt', '');
  });
});
