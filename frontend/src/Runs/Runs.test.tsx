import { screen } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';

import { renderWithProviders, stubApi } from 'testUtils';
import type { RunDetail as RunDetailResource, RunSummary } from 'typings/api';

import RunDetail from './RunDetail';
import Runs from './Runs';

const summary: RunSummary = {
  batchId: 'b1',
  workItemId: 'repository:example/shop',
  approverSubject: 'alice',
  createdAt: '2026-11-09T12:00:00Z',
  state: 'done',
  refusal: null,
  taskCount: 2,
  endedAt: '2026-11-09T12:00:05Z',
  executor: 'executor-1',
};

const detail: RunDetailResource = {
  summary,
  tasks: [
    {
      spec: {
        id: 't1',
        sequence: 1,
        kind: 'validatePullRequest',
        repository: 'example/shop',
        pullRequest: 7,
        headSha: 'cccccccccccccccccccccccccccccccccccccccc',
        approvalId: 'a1',
      },
      result: 'dryRun',
      reason: 'would validate example/shop#7',
    },
    {
      spec: {
        id: 't2',
        sequence: 2,
        kind: 'mergePullRequest',
        repository: 'example/shop',
        pullRequest: 7,
        headSha: 'cccccccccccccccccccccccccccccccccccccccc',
        approvalId: 'a1',
      },
      result: null,
      reason: null,
    },
  ],
  events: [
    {
      sequence: 1,
      at: '2026-11-09T12:00:01Z',
      type: 'run.claimed',
      taskId: null,
      detail: 'executor executor-1, 2 tasks',
      previousHash: '0',
      hash: 'aaaaaaaa11111111',
    },
    {
      sequence: 2,
      at: '2026-11-09T12:00:02Z',
      type: 'run.second-factor',
      taskId: null,
      detail: 'code of alice verified',
      previousHash: 'aaaaaaaa11111111',
      hash: 'bbbbbbbb22222222',
    },
    {
      sequence: 3,
      at: '2026-11-09T12:00:03Z',
      type: 'something.new',
      taskId: null,
      detail: null,
      previousHash: 'bbbbbbbb22222222',
      hash: 'cccccccc33333333',
    },
  ],
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Runs', () => {
  it('lists the runs with their state in words and a link to each', async () => {
    stubApi({
      '/api/v1/runs': [
        summary,
        { ...summary, batchId: 'b2', state: 'refused', refusal: 'secondFactor' },
        { ...summary, batchId: 'b3', state: 'waiting', executor: null },
      ],
    });

    renderWithProviders(<Runs />);

    expect(await screen.findByText('Done')).toBeInTheDocument();
    expect(screen.getByText('Refused')).toBeInTheDocument();
    expect(screen.getByText('Waiting for the executor')).toBeInTheDocument();
    expect(screen.getByText('Refused: the code of the second factor was not accepted')).toBeInTheDocument();
    expect(screen.getAllByText('example/shop')).toHaveLength(3);
    expect(screen.getAllByRole('link', { name: 'Open' })[0]).toHaveAttribute('href', '/runs/b1');
  });

  it('says there is no run yet', async () => {
    stubApi({ '/api/v1/runs': [] });

    renderWithProviders(<Runs />);

    expect(await screen.findByText(/No run yet/)).toBeInTheDocument();
  });

  it('says when the runs cannot be read', async () => {
    stubApi({});

    renderWithProviders(<Runs />);

    expect(await screen.findByText(/Cannot read the runs/)).toBeInTheDocument();
  });
});

describe('RunDetail', () => {
  const renderAt = () =>
    renderWithProviders(
      <Routes>
        <Route path="/runs/:id" element={<RunDetail />} />
        <Route path="*" element={<RunDetail />} />
      </Routes>,
    );

  it('shows the tasks and what became of each', async () => {
    stubApi({ '/api/v1/runs/': detail });
    // The router of the tests starts at "/", so the screen reads the run of an empty id: answer it
    stubApi({ '/api/v1/runs/': detail });

    renderAt();

    expect(await screen.findByText(/Validate example\/shop#7 at ccccccc/)).toBeInTheDocument();
    expect(screen.getByText('dry run')).toBeInTheDocument();
    expect(screen.getByText('not run yet')).toBeInTheDocument();
    expect(screen.getByText('would validate example/shop#7')).toBeInTheDocument();
  });

  it('shows the trace as a timeline in words, with the short hash that chains each line', async () => {
    stubApi({ '/api/v1/runs/': detail });

    renderAt();

    expect(await screen.findByText('Taken by the executor')).toBeInTheDocument();
    expect(screen.getByText('Second factor')).toBeInTheDocument();
    expect(screen.getByText('code of alice verified')).toBeInTheDocument();
    expect(screen.getByText('aaaaaaaa')).toHaveAttribute('title', 'aaaaaaaa11111111');
    expect(screen.getByText('something.new')).toBeInTheDocument();
    expect(screen.getByText(/breaks the chain/)).toBeInTheDocument();
  });

  it('shows a validation that ran: the result of the task and the end of each step with its line breaks', async () => {
    const types = ['task.started', 'task.step', 'task.failed'];
    const details = [
      'validating example/shop#7 at ccccccc in node:22-alpine',
      'npm test: failed, exit 1, 2 s\n1 failing\n  expected 200',
      'npm test failed (exit 1)',
    ];
    stubApi({
      '/api/v1/runs/': {
        ...detail,
        tasks: detail.tasks.map((task, index) =>
          index === 0
            ? { ...task, result: 'passed', reason: '2 steps passed' }
            : { ...task, result: 'failed', reason: 'npm test failed (exit 1)' },
        ),
        events: detail.events.map((event, index) => ({
          ...event,
          type: types[index] ?? event.type,
          detail: details[index] ?? event.detail,
        })),
      },
    });

    renderAt();

    expect(await screen.findByText('passed')).toBeInTheDocument();
    expect(screen.getByText('failed')).toBeInTheDocument();
    expect(screen.getByText('Task started')).toBeInTheDocument();
    expect(screen.getByText('Step')).toBeInTheDocument();
    expect(screen.getByText('Task failed')).toBeInTheDocument();
    expect(screen.getByText(/1 failing/)).toHaveTextContent('npm test: failed, exit 1, 2 s 1 failing expected 200');
  });

  it('shows what an agent does set apart, and a patch as a diff with what is removed and what is added', async () => {
    const types = ['task.agent', 'task.agent', 'task.patch'];
    const details = [
      'says: The route uses a bare star.',
      "edits server.js\n- app.get('*')\n+ app.get('/{*splat}')",
      'patch 1/1 (1 file(s))\n@@ -64,7 +64,7 @@\n-old line\n+new line',
    ];
    stubApi({
      '/api/v1/runs/': {
        ...detail,
        events: detail.events.map((event, index) => ({
          ...event,
          type: types[index] ?? event.type,
          detail: details[index] ?? event.detail,
        })),
      },
    });

    renderAt();

    expect(await screen.findByText(/The route uses a bare star/)).toBeInTheDocument();
    expect(screen.getAllByText('Agent')).toHaveLength(2);
    expect(screen.getByText(/app\.get\('\*'\)/)).toHaveClass('removed');
    expect(screen.getByText(/app\.get\('\/\{\*splat\}'\)/)).toHaveClass('added');
    expect(screen.getByText('-old line')).toHaveClass('removed');
    expect(screen.getByText('+new line')).toHaveClass('added');
    expect(screen.getByText('Patch (nothing was pushed)')).toBeInTheDocument();
  });

  it('says who approved it and which executor took it', async () => {
    stubApi({ '/api/v1/runs/': detail });

    renderAt();

    expect(await screen.findByText(/Approved by alice/)).toBeInTheDocument();
    expect(screen.getByText(/Executor: executor-1/)).toBeInTheDocument();
  });

  it('says when no executor took it, and why a refused run was refused', async () => {
    stubApi({
      '/api/v1/runs/': {
        ...detail,
        summary: { ...summary, state: 'refused', refusal: 'journalMismatch', executor: null },
        events: [],
      },
    });

    renderAt();

    expect(await screen.findByText(/No executor has taken it yet/)).toBeInTheDocument();
    expect(screen.getByText(/the batch is not the one that was approved/)).toBeInTheDocument();
    expect(screen.getByText('Nothing in the trace yet.')).toBeInTheDocument();
  });

  it('says there is no such run', async () => {
    stubApi({});

    renderAt();

    expect(await screen.findByText('No such run.')).toBeInTheDocument();
  });
});
