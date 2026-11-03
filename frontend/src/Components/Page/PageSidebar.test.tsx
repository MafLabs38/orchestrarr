import { screen, waitFor } from '@testing-library/react';

import { renderWithProviders, report, stubApi, workItem } from 'testUtils';

import PageSidebar from './PageSidebar';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PageSidebar', () => {
  it('counts the decisions waiting', async () => {
    stubApi({ '/api/v1/work-items': report({ items: [workItem, { ...workItem, id: 'b' }] }) });

    renderWithProviders(<PageSidebar />);

    expect(await screen.findByText('2')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Decisions/ })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Status/ })).toBeInTheDocument();
  });

  it('shows no counter when nothing is waiting, once the answer has arrived', async () => {
    const fetchMock = stubApi({ '/api/v1/work-items': report({ items: [] }) });

    renderWithProviders(<PageSidebar />);

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    await waitFor(() => expect(fetchMock.mock.results.every((result) => result.type === 'return')).toBe(true));
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(screen.getByRole('link', { name: 'Decisions' })).toBeInTheDocument();
    expect(screen.queryByText('0')).not.toBeInTheDocument();
  });

  it('counts the approvals in the queue and links to the activity', async () => {
    stubApi({
      '/api/v1/work-items': report({ items: [] }),
      '/api/v1/approvals': [{ id: 'a' }, { id: 'b' }, { id: 'c' }],
    });

    renderWithProviders(<PageSidebar />);

    expect(await screen.findByText('3')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Queue/ })).toHaveAttribute('href', '/queue');
    expect(screen.getByRole('link', { name: 'Activity' })).toHaveAttribute('href', '/system/activity');
  });
});
