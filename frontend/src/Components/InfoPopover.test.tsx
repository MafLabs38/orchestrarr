import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';

import InfoPopover from './InfoPopover';

describe('InfoPopover', () => {
  it('opens the long text on a click and closes it on a second click, on Escape and on a click elsewhere', async () => {
    render(
      <div>
        <InfoPopover label="Why: a thing">The long explanation.</InfoPopover>
        <button type="button">elsewhere</button>
      </div>,
    );
    expect(screen.queryByRole('note')).not.toBeInTheDocument();
    const button = screen.getByRole('button', { name: 'Why: a thing' });

    await userEvent.click(button);
    expect(screen.getByRole('note')).toHaveTextContent('The long explanation.');
    expect(button).toHaveAttribute('aria-expanded', 'true');

    await userEvent.click(button);
    expect(screen.queryByRole('note')).not.toBeInTheDocument();

    await userEvent.click(button);
    await userEvent.keyboard('{Escape}');
    expect(screen.queryByRole('note')).not.toBeInTheDocument();

    await userEvent.click(button);
    await userEvent.click(screen.getByRole('button', { name: 'elsewhere' }));
    expect(screen.queryByRole('note')).not.toBeInTheDocument();
  });
});
