import { screen } from '@testing-library/react';

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

  it('shows no counter when nothing is waiting', async () => {
    stubApi({ '/api/v1/work-items': report({ items: [] }) });

    renderWithProviders(<PageSidebar />);

    await screen.findByRole('link', { name: /Decisions/ });
    expect(screen.queryByText('0')).not.toBeInTheDocument();
  });
});
