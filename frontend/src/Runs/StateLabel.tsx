import Label from 'Components/Label';
import { type Kind, kinds } from 'Helpers/Props/kinds';
import type { BatchState } from 'typings/api';
import { describeBatchState } from 'Utilities/describe';

const stateKinds: Record<BatchState, Kind> = {
  waiting: kinds.INFO,
  delivered: kinds.WARNING,
  done: kinds.SUCCESS,
  refused: kinds.DANGER,
  expired: kinds.DEFAULT,
};

// Where a run is, as a coloured word: the colour is never the only thing that says it.
export default function StateLabel({ state }: Readonly<{ state: BatchState }>) {
  return <Label kind={stateKinds[state]}>{describeBatchState(state)}</Label>;
}
