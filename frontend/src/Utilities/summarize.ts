import type { Finding, WorkItem } from 'typings/api';
import translate from 'Utilities/translate';

import { describeUpdateKind, isReadyToMerge } from './describe';

const order: Record<Finding['severity'], number> = { danger: 0, warning: 1, info: 2 };

// The worst alerts, when there are critical or high ones: "1 critical", else "2 high"
function worstAlerts(finding: Finding): string | null {
  if (finding.alerts?.critical) {
    return translate('AlertCritical', { count: finding.alerts.critical });
  }
  return finding.alerts?.high ? translate('AlertHigh', { count: finding.alerts.high }) : null;
}

function alerts(
  finding: Finding,
  one: 'SummaryAlertsOne' | 'SummaryCodeAlertsOne',
  other: 'SummaryAlertsOther' | 'SummaryCodeAlertsOther',
): string {
  const text = translate(finding.count === 1 ? one : other, { count: finding.count });
  const worst = worstAlerts(finding);
  return worst ? `${text} (${worst})` : text;
}

function pullRequests(item: WorkItem, finding: Finding): string {
  const text = translate(finding.count === 1 ? 'SummaryPullRequestsOne' : 'SummaryPullRequestsOther', {
    count: finding.count,
  });
  if (item.pullRequests === null) {
    return text;
  }
  return `${text} (${translate('SummaryPullRequestsReady', { ready: item.pullRequests.filter(isReadyToMerge).length })})`;
}

function summarizeFinding(item: WorkItem, finding: Finding): string {
  switch (finding.kind) {
    case 'dependencyAlerts':
      return alerts(finding, 'SummaryAlertsOne', 'SummaryAlertsOther');
    case 'codeScanningAlerts':
      return alerts(finding, 'SummaryCodeAlertsOne', 'SummaryCodeAlertsOther');
    case 'secretScanningAlerts':
      return translate(finding.count === 1 ? 'SummarySecretsOne' : 'SummarySecretsOther', { count: finding.count });
    case 'openDependencyPullRequests':
      return pullRequests(item, finding);
    case 'failingCi':
      return translate('SummaryCiFailing');
    case 'outdatedImage': {
      const change = finding.image ? describeChange(finding) : null;
      return change ? translate('SummaryImageChange', { change }) : translate('SummaryImageUpdate');
    }
    case 'dependencyScanUnavailable':
      return translate('SummaryScanUnavailable');
  }
}

// "major" for an image that moved by a major version, or nothing when the move is not typed
function describeChange(finding: Finding): string | null {
  const kind = finding.image?.change;
  if (!kind || kind === 'none' || kind === 'build') {
    return null;
  }
  return describeUpdateKind(kind)?.replace(/ update$/, '') ?? null;
}

// What is wrong with an application, in a few words, most serious first: the line of the list.
export function summarize(item: WorkItem): string[] {
  return [...item.findings]
    .sort((a, b) => order[a.severity] - order[b.severity])
    .map((finding) => summarizeFinding(item, finding));
}

// What approving would do, in a few words: "3 of 3 steps", or that only a person can act.
export function summarizePlan(item: WorkItem): string {
  if (item.plan.length === 0) {
    return translate('SummaryNeedsAPerson');
  }
  const ticked = item.plan.filter((step) => step.ticked && step.available).length;
  return translate('SummarySteps', { ticked, total: item.plan.length });
}
