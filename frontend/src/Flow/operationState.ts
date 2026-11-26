import type { LucideIcon } from 'lucide-react';

import * as icons from 'Helpers/Props/icons';
import { type Kind, kinds } from 'Helpers/Props/kinds';
import type { EquipmentState, FlowProblem, OperationState } from 'typings/api';
import translate from 'Utilities/translate';

// What an operation does, as a word, a kind and an icon: a state is never told by its colour alone (DESIGN §5)
export interface StateLook {
  text: string;
  kind: Kind;
  Icon: LucideIcon;
  waiting: boolean;
}

export function operationLook(state: OperationState): StateLook {
  switch (state) {
    case 'working':
      return { text: translate('OperationWorking'), kind: kinds.INFO, Icon: icons.WORKING, waiting: false };
    case 'judging':
      return { text: translate('OperationJudging'), kind: kinds.PURPLE, Icon: icons.JUDGING, waiting: false };
    case 'waitingMachine':
      return { text: translate('OperationWaitingMachine'), kind: kinds.WARNING, Icon: icons.WAITING, waiting: true };
    case 'waitingLane':
      return { text: translate('OperationWaitingLane'), kind: kinds.WARNING, Icon: icons.WAITING, waiting: true };
    case 'waitingRunner':
      return { text: translate('OperationWaitingRunner'), kind: kinds.DANGER, Icon: icons.WAITING, waiting: true };
    case 'waitingTurn':
      return { text: translate('OperationWaitingTurn'), kind: kinds.DEFAULT, Icon: icons.WAITING_TURN, waiting: true };
    case 'waitingBot':
      return { text: translate('OperationWaitingBot'), kind: kinds.PINK, Icon: icons.WAITING_BOT, waiting: true };
    case 'done':
      return { text: translate('OperationDone'), kind: kinds.SUCCESS, Icon: icons.DONE, waiting: false };
    case 'putAside':
      return { text: translate('OperationPutAside'), kind: kinds.DANGER, Icon: icons.PUT_ASIDE, waiting: false };
    default:
      return { text: translate('OperationWaitingPickup'), kind: kinds.DEFAULT, Icon: icons.PENDING, waiting: true };
  }
}

export function equipmentLook(state: EquipmentState): { text: string; kind: Kind } {
  switch (state) {
    case 'inProcess':
      return { text: translate('EquipmentInProcess'), kind: kinds.INFO };
    case 'idle':
      return { text: translate('EquipmentIdle'), kind: kinds.SUCCESS };
    case 'qualifying':
      return { text: translate('EquipmentQualifying'), kind: kinds.WARNING };
    default:
      return { text: translate('EquipmentDown'), kind: kinds.DANGER };
  }
}

// The name of a place of the flow: the line before a start, a world (a workshop), or what is over
export function placeName(place: string): string {
  switch (place) {
    case 'queue':
      return translate('FlowPlaceLine');
    case 'out':
      return translate('FlowPlaceOut');
    case 'Staging':
      return translate('FlowWorkshopStaging');
    case 'Git':
      return translate('FlowWorkshopForge');
    case 'Release':
      return translate('FlowWorkshopRelease');
    default:
      return place;
  }
}

// What the table says of an alert row is a number (the count of the alerts): it is told as what it is
export function operationTitle(title: string, kind: string | null): string {
  if (!/^\d+$/.test(title)) {
    return title;
  }
  if (kind === 'DependencyAlert') {
    return translate('FlowTitleAlerts', { count: title });
  }
  return kind === 'FailingCi' ? translate('FlowTitlePipelineRed') : title;
}

// What a problem that is not over does now, in the words of the Flow screen (the same rule as the factory view of the host): null when it is over
export function operationStateOf(problem: FlowProblem): OperationState | null {
  const code = problem.activity?.waiting?.code;
  if (problem.state === 'queued') {
    return code === 'bot' ? 'waitingBot' : 'waitingTurn';
  }
  if (problem.state !== 'inProgress') {
    return null;
  }
  switch (problem.activity?.kind) {
    case 'running':
      return 'working';
    case 'controller':
      return 'judging';
    default:
      break;
  }
  switch (code) {
    case 'machine':
      return 'waitingMachine';
    case 'busy':
      return 'waitingLane';
    case 'turn':
      return 'waitingTurn';
    case 'bot':
      return 'waitingBot';
    case 'runner':
      return 'waitingRunner';
    default:
      return 'waitingPickup';
  }
}
