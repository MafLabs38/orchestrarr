import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import {
  approvalsOff,
  finding,
  pullRequest,
  pullRequestsRead,
  renderWithProviders,
  report,
  status,
  stubApi,
  workItem,
} from 'testUtils';

import Decisions from './Decisions';
import { intervalFor } from './useWorkItems';

const path = '/api/v1/work-items';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Decisions', () => {
  it('shows a card per work item', async () => {
    stubApi({ [path]: report({ items: [workItem, { ...workItem, id: 'image:x', title: 'grafana/grafana:11' }] }) });

    renderWithProviders(<Decisions />);

    expect(await screen.findByRole('button', { name: /example\/shop/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /grafana\/grafana:11/ })).toBeInTheDocument();
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

    expect(await screen.findByRole('button', { name: /example\/shop/ })).toBeInTheDocument();
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
    await screen.findByRole('button', { name: /example\/shop/ });
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    expect(await screen.findByText(/Nothing needs a decision/)).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith(`${path}?refresh=true`, expect.anything());
  });

  it('sends nothing but reads while approving is turned off, whatever the operator does', async () => {
    const waiting = { ...finding, kind: 'openDependencyPullRequests' as const, count: 2, alerts: null };
    const withPullRequests = {
      ...workItem,
      findings: [waiting],
      pullRequests: pullRequestsRead({ items: [pullRequest(1), pullRequest(2)] }),
    };
    const fetchMock = stubApi({
      [path]: report({ items: [withPullRequests] }),
      [`${path}?refresh=true`]: report({ items: [withPullRequests] }),
      '/api/v1/system/status': status({}, approvalsOff),
    });

    renderWithProviders(<Decisions />);
    await screen.findByRole('link', { name: 'Pull request #1' });
    await userEvent.click(screen.getByRole('checkbox', { name: 'Build and test the change' }));
    await userEvent.click(screen.getByRole('button', { name: 'Approve' }));
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    expect(fetchMock.mock.calls.length).toBeGreaterThan(1);
    const calls = fetchMock.mock.calls as unknown as [string, RequestInit | undefined][];
    expect(calls.every(([, init]) => !init?.method || init.method === 'GET')).toBe(true);
  });

  it('reads again soon while pull requests are being read, and at the usual pace otherwise', () => {
    const pending = { ...workItem, pullRequests: pullRequestsRead({ status: 'pending', items: [] }) };

    expect(intervalFor(report({ items: [pending] }))).toBe(3000);
    expect(intervalFor(report({ items: [workItem] }))).toBe(30000);
    expect(intervalFor(undefined)).toBe(30000);
  });

  describe('as a list of many applications', () => {
    const many = [
      { ...workItem, id: 'a', title: 'example/a', severity: 'danger' as const },
      { ...workItem, id: 'b', title: 'example/b', severity: 'danger' as const },
      { ...workItem, id: 'c', title: 'example/c', severity: 'warning' as const },
      { ...workItem, id: 'd', title: 'example/d', severity: 'info' as const },
      { ...workItem, id: 'e', title: 'example/e', severity: 'info' as const },
    ];

    const renderMany = () => {
      stubApi({ [path]: report({ items: many }) });
      return renderWithProviders(<Decisions />);
    };

    it('shows a line per application, all closed, grouped by severity with their counts', async () => {
      renderMany();

      expect(await screen.findByRole('button', { name: /example\/a/ })).toHaveAttribute('aria-expanded', 'false');
      expect(screen.getAllByRole('button', { expanded: false })).toHaveLength(5);
      expect(screen.getByRole('heading', { level: 2, name: 'Urgent · 2' })).toBeInTheDocument();
      expect(screen.getByRole('heading', { level: 2, name: 'Attention · 1' })).toBeInTheDocument();
      expect(screen.getByRole('heading', { level: 2, name: 'Routine · 2' })).toBeInTheDocument();
      expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
      expect(screen.getByText('5 of 5 applications')).toBeInTheDocument();
    });

    it('opens one application at a time', async () => {
      renderMany();

      await userEvent.click(await screen.findByRole('button', { name: /example\/c/ }));

      expect(screen.getByRole('button', { name: /example\/c/ })).toHaveAttribute('aria-expanded', 'true');
      expect(screen.getByRole('button', { name: /example\/a/ })).toHaveAttribute('aria-expanded', 'false');
      expect(screen.getAllByRole('checkbox')).toHaveLength(1);

      await userEvent.click(screen.getByRole('button', { name: /example\/c/ }));
      expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
    });

    it('opens and closes them all', async () => {
      renderMany();
      await screen.findByRole('button', { name: /example\/a/ });

      await userEvent.click(screen.getByRole('button', { name: 'Expand all' }));
      expect(screen.getAllByRole('checkbox')).toHaveLength(5);

      await userEvent.click(screen.getByRole('button', { name: 'Collapse all' }));
      expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
    });

    it('shows only one severity when asked, and says how many of the total', async () => {
      renderMany();
      await screen.findByRole('button', { name: /example\/a/ });

      await userEvent.selectOptions(screen.getByLabelText('Show'), 'info');

      expect(screen.getAllByRole('button', { expanded: false })).toHaveLength(2);
      expect(screen.queryByRole('button', { name: /example\/a/ })).not.toBeInTheDocument();
      expect(screen.queryByRole('heading', { level: 2, name: /Urgent/ })).not.toBeInTheDocument();
      expect(screen.getByText('2 of 5 applications')).toBeInTheDocument();
    });

    it('expands only what is shown when a filter is on', async () => {
      renderMany();
      await screen.findByRole('button', { name: /example\/a/ });

      await userEvent.selectOptions(screen.getByLabelText('Show'), 'danger');
      await userEvent.click(screen.getByRole('button', { name: 'Expand all' }));
      await userEvent.selectOptions(screen.getByLabelText('Show'), 'all');

      expect(screen.getAllByRole('checkbox')).toHaveLength(2);
      expect(screen.getByRole('button', { name: /example\/e/ })).toHaveAttribute('aria-expanded', 'false');
    });

    it('cannot filter on a severity nobody has', async () => {
      stubApi({ [path]: report({ items: many.filter((m) => m.severity !== 'warning') }) });
      renderWithProviders(<Decisions />);
      await screen.findByRole('button', { name: /example\/a/ });

      expect(screen.getByRole('option', { name: 'Attention · 0' })).toBeDisabled();
    });
  });

  it('opens the cards by default when there are only a few applications', async () => {
    stubApi({ [path]: report({ items: [workItem, { ...workItem, id: 'b', title: 'example/b' }] }) });

    renderWithProviders(<Decisions />);

    expect(await screen.findByRole('button', { name: /example\/b/ })).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getAllByRole('checkbox')).toHaveLength(2);
  });
});
