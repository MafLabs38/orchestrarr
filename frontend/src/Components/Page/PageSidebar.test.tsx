import { screen, waitFor, within } from '@testing-library/react';

import { problem, problemsReport, renderWithProviders, stubApi } from 'testUtils';

import PageSidebar from './PageSidebar';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PageSidebar', () => {
  it('counts the decisions waiting', async () => {
    stubApi({ '/api/v1/problems': problemsReport([problem(), problem({ id: 'pr:example/shop#3' })]) });

    renderWithProviders(<PageSidebar />);

    expect(await screen.findByText('2')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Decisions/ })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Status/ })).toBeInTheDocument();
  });

  it('counts only what can be decided, not the rows to configure or to know', async () => {
    const toKnow = problem({ id: 'finding:x', goesUpTo: 'noAction', why: 'noActionDefined' });
    stubApi({ '/api/v1/problems': problemsReport([problem(), toKnow, { ...toKnow, id: 'finding:y' }]) });

    renderWithProviders(<PageSidebar />);

    const link = await screen.findByRole('link', { name: /Decisions/ });
    expect(await within(link).findByText('1')).toBeInTheDocument();
    expect(within(link).queryByText('3')).not.toBeInTheDocument();
  });

  it('shows no counter when nothing is waiting, once the answer has arrived', async () => {
    const fetchMock = stubApi({ '/api/v1/problems': problemsReport([]) });

    renderWithProviders(<PageSidebar />);

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    await waitFor(() => expect(fetchMock.mock.results.every((result) => result.type === 'return')).toBe(true));
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(screen.getByRole('link', { name: 'Decisions' })).toBeInTheDocument();
    expect(screen.queryByText('0')).not.toBeInTheDocument();
  });

  it('counts the approvals in the queue and links to the activity', async () => {
    stubApi({
      '/api/v1/problems': problemsReport([]),
      '/api/v1/approvals': [{ id: 'a' }, { id: 'b' }, { id: 'c' }],
    });

    renderWithProviders(<PageSidebar />);

    expect(await screen.findByText('3')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Queue/ })).toHaveAttribute('href', '/queue');
    expect(screen.getByRole('link', { name: 'Activity' })).toHaveAttribute('href', '/system/activity');
  });
});
