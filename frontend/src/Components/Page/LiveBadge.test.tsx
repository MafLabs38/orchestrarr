import { act, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { liveStatus } from 'Live/liveStatus';

import LiveBadge from './LiveBadge';

describe('LiveBadge', () => {
  it('says whether the page follows what happens, and follows the state of the channel', () => {
    liveStatus.set('live');
    render(<LiveBadge />);
    expect(screen.getByText('Live')).toBeInTheDocument();

    act(() => liveStatus.set('reconnecting'));
    expect(screen.getByText('Reconnecting…')).toBeInTheDocument();

    act(() => liveStatus.set('off'));
    expect(screen.getByText('Not live')).toBeInTheDocument();
    expect(screen.getByTitle(/reads again every thirty seconds/)).toBeInTheDocument();
  });
});
