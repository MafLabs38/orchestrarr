import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import AppCell, { initialsOf, shortName } from './AppCell';

describe('AppCell', () => {
  it('shows the repository without its owner and gives the full name when the pointer rests on it', () => {
    render(<AppCell application="MafLabs38/orchestrarr-testbed" />);

    expect(screen.getByText('orchestrarr-testbed')).toBeInTheDocument();
    expect(screen.getByTitle('MafLabs38/orchestrarr-testbed')).toBeInTheDocument();
  });

  it.each([
    ['example/shop', 'sh', 'shop'],
    ['example/web-portal', 'wp', 'web-portal'],
    ['example/notes_desktop', 'nd', 'notes_desktop'],
    ['plain', 'pl', 'plain'],
  ])('names %s by two letters and its short name', (application, initials, short) => {
    expect(initialsOf(application)).toBe(initials.toUpperCase());
    expect(shortName(application)).toBe(short);
  });
});
