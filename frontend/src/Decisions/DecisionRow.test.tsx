import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { finding, renderWithProviders, step, stubApi, workItem } from 'testUtils';
import type { WorkItem } from 'typings/api';

import DecisionRow from './DecisionRow';

const item: WorkItem = {
  ...workItem,
  findings: [{ ...finding, count: 4, alerts: { critical: 1, high: 2, medium: 1, low: 0 } }],
  plan: [step(), step({ kind: 'mergePullRequests' })],
};

beforeEach(() => {
  stubApi({});
});

afterEach(() => {
  vi.unstubAllGlobals();
});

const renderRow = (expanded: boolean, onToggle = vi.fn()) =>
  renderWithProviders(
    <ul>
      <DecisionRow item={item} expanded={expanded} onToggle={onToggle} />
    </ul>,
  );

describe('DecisionRow', () => {
  it('says what is wrong in a line: the name, the severity in words, the summary and the plan', () => {
    renderRow(false);

    const button = screen.getByRole('button', { name: /example\/shop/ });
    expect(button).toHaveTextContent('Urgent');
    expect(button).toHaveTextContent('4 alerts (1 critical)');
    expect(button).toHaveTextContent('2 of 2 steps');
  });

  it('is closed: the plan and the findings are not in the page, and the button says it', () => {
    renderRow(false);

    expect(screen.getByRole('button', { name: /example\/shop/ })).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
    expect(screen.queryByText('Findings')).not.toBeInTheDocument();
  });

  it('opens onto the card, without repeating the name that the line already shows', () => {
    renderRow(true);

    expect(screen.getByRole('button', { name: /example\/shop/ })).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getAllByRole('checkbox')).toHaveLength(2);
    expect(screen.getByText('Findings')).toBeInTheDocument();
    expect(screen.getAllByText('example/shop')).toHaveLength(1);
  });

  it('asks to be toggled when the line is clicked, from the keyboard too', async () => {
    const onToggle = vi.fn();
    renderRow(false, onToggle);

    await userEvent.click(screen.getByRole('button', { name: /example\/shop/ }));
    screen.getByRole('button', { name: /example\/shop/ }).focus();
    await userEvent.keyboard('{Enter}');

    expect(onToggle).toHaveBeenCalledTimes(2);
  });

  it.each([
    ['danger', 'Urgent'],
    ['warning', 'Attention'],
    ['info', 'Routine'],
  ] as const)('says a %s application in words', (severity, text) => {
    renderWithProviders(
      <ul>
        <DecisionRow item={{ ...item, severity }} expanded={false} onToggle={vi.fn()} />
      </ul>,
    );

    expect(screen.getByRole('button', { name: /example\/shop/ })).toHaveTextContent(text);
  });

  it('has nothing to control while it is closed', () => {
    renderRow(false);

    expect(screen.getByRole('button', { name: /example\/shop/ })).not.toHaveAttribute('aria-controls');
  });

  it('controls the region it opens', () => {
    renderRow(true);

    const controlled = screen.getByRole('button', { name: /example\/shop/ }).getAttribute('aria-controls');
    expect(controlled).toBeTruthy();
    expect(document.getElementById(controlled ?? '')).toContainElement(screen.getByText('Findings'));
  });
});
