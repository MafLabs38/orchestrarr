import type { Kind } from 'Helpers/Props/kinds';
import { kinds } from 'Helpers/Props/kinds';
import translate from 'Utilities/translate';

// An event of the audit trail, as a person reads it on the traveler (ADR 0056): a few words and a kind; an event that is not known is shown as it is
const events: Record<string, { key: Parameters<typeof translate>[0]; kind: Kind }> = {
  'problem.queued': { key: 'StepQueued', kind: kinds.DEFAULT },
  'problem.started': { key: 'StepStarted', kind: kinds.INFO },
  'lead.planned': { key: 'StepPlanned', kind: kinds.DEFAULT },
  'lead.refused': { key: 'StepPlanRefused', kind: kinds.DANGER },
  'mandate.issued': { key: 'StepMandateIssued', kind: kinds.DEFAULT },
  'mandate.revoked': { key: 'StepMandateRevoked', kind: kinds.DEFAULT },
  'mission.handed-over': { key: 'StepHandedOver', kind: kinds.INFO },
  'controller.decided': { key: 'StepDecided', kind: kinds.PURPLE },
  'mission.sent-back': { key: 'StepSentBack', kind: kinds.WARNING },
  'mission.accepted': { key: 'StepAccepted', kind: kinds.SUCCESS },
  'mission.ended': { key: 'StepMissionEnded', kind: kinds.WARNING },
  'act.authorised': { key: 'StepActAuthorised', kind: kinds.DEFAULT },
  'act.performed': { key: 'StepActPerformed', kind: kinds.INFO },
  'act.refused': { key: 'StepActRefused', kind: kinds.WARNING },
  'problem.parked': { key: 'StepParked', kind: kinds.PINK },
  'problem.bot-rebase-requested': { key: 'StepBotAsked', kind: kinds.PINK },
  'problem.followed-bot': { key: 'StepFollowedBot', kind: kinds.PINK },
  'problem.held': { key: 'StepHeld', kind: kinds.WARNING },
  'problem.released': { key: 'StepReleased', kind: kinds.INFO },
  'problem.priority-set': { key: 'StepPrioritySet', kind: kinds.DEFAULT },
  'problem.priority-raised': { key: 'StepPriorityRaised', kind: kinds.WARNING },
  'problem.queue-refused': { key: 'StepLeftLine', kind: kinds.DANGER },
  'problem.closed': { key: 'StepClosed', kind: kinds.SUCCESS },
  'problem.failed': { key: 'StepFailed', kind: kinds.DANGER },
  'problem.escalated': { key: 'StepEscalated', kind: kinds.DANGER },
};

export function describeStep(event: string): { text: string; kind: Kind } {
  const known = events[event];
  return known ? { text: translate(known.key), kind: known.kind } : { text: event, kind: kinds.DEFAULT };
}
