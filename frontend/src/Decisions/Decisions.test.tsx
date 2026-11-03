import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { renderWithProviders, report, stubApi, workItem } from 'testUtils';

import Decisions from './Decisions';

const path = '/api/v1/work-items';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Decisions', () => {
  it('shows a card per work item', async () => {
    stubApi({ [path]: report({ items: [workItem, { ...workItem, id: 'image:x', title: 'grafana/grafana:11' }] }) });

    renderWithProviders(<Decisions />);

    expect(await screen.findByRole('heading', { name: 'example/shop' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'grafana/grafana:11' })).toBeInTheDocument();
  });

  it('says nothing needs a decision when the fleet is fine', async () => {
    stubApi({ [path]: report({ items: [] }) });

    renderWithProviders(<Decisions />);

    expect(await screen.findByText(/Nothing needs a decision/)).toBeInTheDocument();
  });

  it('says no source is configured instead of claiming the fleet is fine', async () => {
    stubApi({ [path]: report({ items: [], sourceKind: 'none' }) });

    renderWithProviders(<Decisions />);

    expect(await screen.findByText(/No source of findings is configured/)).toBeInTheDocument();
    expect(screen.queryByText(/Nothing needs a decision/)).not.toBeInTheDocument();
  });

  it('words the code of the error when the source cannot be read and there is nothing to show', async () => {
    stubApi({ [path]: report({ items: [], error: 'unreachable' }) });

    renderWithProviders(<Decisions />);

    expect(await screen.findByRole('alert')).toHaveTextContent('Cannot read the fleet: The source cannot be reached');
  });

  it('keeps the cards of the last read and warns when the last read failed', async () => {
    stubApi({ [path]: report({ error: 'timeout', stale: true }) });

    renderWithProviders(<Decisions />);

    expect(await screen.findByRole('heading', { name: 'example/shop' })).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent('The last read failed: The source did not answer in time');
  });

  it('shows an error when the API itself answers with a failure', async () => {
    stubApi({});

    renderWithProviders(<Decisions />);

    expect(await screen.findByRole('alert')).toHaveTextContent('404');
  });

  it('reads the source again when asked to refresh', async () => {
    const fetchMock = stubApi({ [path]: report(), [`${path}?refresh=true`]: report({ items: [] }) });

    renderWithProviders(<Decisions />);
    await screen.findByRole('heading', { name: 'example/shop' });
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    expect(await screen.findByText(/Nothing needs a decision/)).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith(`${path}?refresh=true`, expect.anything());
  });

  it('never sends anything but a read, whatever the operator does', async () => {
    const fetchMock = stubApi({ [path]: report(), [`${path}?refresh=true`]: report() });

    renderWithProviders(<Decisions />);
    await screen.findByRole('heading', { name: 'example/shop' });
    await userEvent.click(screen.getByRole('checkbox', { name: 'Build and test the change' }));
    await userEvent.click(screen.getByRole('button', { name: 'Approve and run' }));
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    expect(fetchMock.mock.calls.length).toBeGreaterThan(1);
    const calls = fetchMock.mock.calls as unknown as [string, RequestInit | undefined][];
    expect(calls.every(([, init]) => !init?.method || init.method === 'GET')).toBe(true);
  });
});
