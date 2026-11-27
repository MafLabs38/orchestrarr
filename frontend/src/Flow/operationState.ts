import type { LucideIcon } from 'lucide-react';

import * as icons from 'Helpers/Props/icons';
import { type Kind, kinds } from 'Helpers/Props/kinds';
import type { BoardOperation, EquipmentState, FlowBoard, OperationState } from 'typings/api';
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
    case 'planning':
      return { text: translate('OperationPlanning'), kind: kinds.DEFAULT, Icon: icons.AI, waiting: true };
    case 'held':
      return { text: translate('OperationHeld'), kind: kinds.WARNING, Icon: icons.HELD, waiting: true };
    case 'done':
      return { text: translate('OperationDone'), kind: kinds.SUCCESS, Icon: icons.DONE, waiting: false };
    case 'putAside':
      return { text: translate('OperationPutAside'), kind: kinds.DANGER, Icon: icons.PUT_ASIDE, waiting: false };
    case 'escalated':
      return { text: translate('OperationEscalated'), kind: kinds.WARNING, Icon: icons.WARNING, waiting: true };
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

// The short name of a lot: its repository without its owner
export function lotName(repository: string): string {
  return repository.split('/').pop() ?? repository;
}

function placeOrder(operation: BoardOperation): number {
  if (operation.place === 'out') {
    return 2;
  }
  return operation.place === 'queue' ? 1 : 0;
}

// Every operation of the factory view: in process first, then the line, then what is over, the newest first in each (what the History and the decisions taken list)
export function operationsOf(board: FlowBoard | undefined): BoardOperation[] {
  return (board?.lots ?? [])
    .flatMap((lot) => lot.operations)
    .sort((a, b) => placeOrder(a) - placeOrder(b) || Date.parse(b.since) - Date.parse(a.since));
}
