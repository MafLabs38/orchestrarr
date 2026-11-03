import { screen, within } from '@testing-library/react';

import { renderWithProviders, stubApi } from 'testUtils';
import type { ActivityEntry } from 'typings/api';

import Activity from './Activity';

const path = '/api/v1/activity';

const entries: ActivityEntry[] = [
  {
    at: '2026-11-03T10:00:02Z',
    event: 'approval.refused',
    subject: 'dev-operator',
    reference: null,
    detail: 'AlreadyApproved repository:example/shop',
  },
  {
    at: '2026-11-03T10:00:01Z',
    event: 'approval.recorded',
    subject: 'dev-operator',
    reference: 'r1',
    detail: 'example/shop#7@abc steps=Validate',
  },
  { at: '2026-11-03T10:00:00Z', event: 'something.new', subject: 'x', reference: null, detail: null },
];

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Activity', () => {
  it('shows the trail with the events in words, newest first, and who did it', async () => {
    stubApi({ [path]: entries });

    renderWithProviders(<Activity />);

    const rows = await screen.findAllByRole('row');
    expect(rows).toHaveLength(4);
    expect(within(rows[1]).getByText('Approval refused')).toBeInTheDocument();
    expect(within(rows[2]).getByText('Approval recorded')).toBeInTheDocument();
    expect(within(rows[2]).getByText('example/shop#7@abc steps=Validate')).toBeInTheDocument();
    expect(within(rows[1]).getByText('dev-operator')).toBeInTheDocument();
  });

  it('shows an event it does not know as the server names it', async () => {
    stubApi({ [path]: entries });

    renderWithProviders(<Activity />);

    expect(await screen.findByText('something.new')).toBeInTheDocument();
  });

  it('says when nothing happened yet', async () => {
    stubApi({ [path]: [] });

    renderWithProviders(<Activity />);

    expect(await screen.findByText('Nothing happened yet.')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('says when the trail cannot be read', async () => {
    stubApi({});

    renderWithProviders(<Activity />);

    expect(await screen.findByRole('alert')).toHaveTextContent('Cannot read the activity');
  });
});
