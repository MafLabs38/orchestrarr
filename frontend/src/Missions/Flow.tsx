import classNames from 'classnames';

import CapabilityHint from 'Components/CapabilityHint';
import Label from 'Components/Label';
import { operationLook } from 'Flow/operationState';
import { type Kind, kinds } from 'Helpers/Props/kinds';
import { iconSizes } from 'Helpers/Props/sizes';
import type { FlowActivity, FlowProblem, FlowStep, MissionView } from 'typings/api';
import {
  describeAge,
  describeFlowState,
  describeFlowStep,
  describeMissionEnd,
  describeMissionState,
  describeMissionWorld,
} from 'Utilities/describe';
import translate from 'Utilities/translate';

import styles from './Flow.module.css';
import { useNow } from './useFlow';

// A mission that reports nothing for this long is told as quiet: an agent thinks for minutes, but not for this long
const quietAfterMs = 10 * 60 * 1000;

const urgentFrom = 70;

const stateKinds: Record<FlowProblem['state'], Kind> = {
  queued: kinds.DEFAULT,
  inProgress: kinds.WARNING,
  escalated: kinds.WARNING,
  closed: kinds.SUCCESS,
  failed: kinds.DANGER,
  setAside: kinds.DEFAULT,
  inPlan: kinds.DEFAULT,
};

const stepStyles: Record<FlowStep['state'], string> = {
  notStarted: styles.notStarted,
  waiting: styles.waiting,
  running: styles.running,
  reading: styles.reading,
  done: styles.done,
  ended: styles.ended,
};

// The line of a runner starts with the time it was said; the screen tells how long ago instead
function withoutClock(line: string | null): string {
  return line ? line.replace(/^\d{2}:\d{2}:\d{2}\s+/, '') : '';
}

function waitingText(activity: FlowActivity): string {
  const waiting = activity.waiting;
  switch (waiting?.code) {
    case 'turn':
      return waiting.subject
        ? translate('FlowWaitingTurn', { subject: waiting.subject })
        : translate('FlowWaitingTurnUnknown');
    case 'machine':
      return translate('FlowWaitingMachine', { subject: waiting.subject ?? '' });
    case 'busy':
      return translate('FlowWaitingBusy', { subject: waiting.subject ?? '' });
    case 'bot':
      return translate('FlowWaitingBot', { subject: waiting.subject ?? '' });
    case 'held':
      return translate('FlowWaitingHeld', { subject: waiting.subject ?? '' });
    case 'planning':
      return translate('FlowWaitingPlanning');
    case 'runner':
      return translate('FlowWaitingRunner');
    default:
      return translate('FlowWaitingPickup');
  }
}

function ActivityLine({ activity, now }: Readonly<{ activity: FlowActivity; now: number }>) {
  const age = activity.at ? now - Date.parse(activity.at) : null;
  const quiet = activity.kind === 'running' && age !== null && age > quietAfterMs;
  let text: string;
  if (activity.kind === 'running') {
    text = withoutClock(activity.text);
  } else if (activity.kind === 'controller') {
    text = translate('FlowControllerReading');
  } else {
    text = waitingText(activity);
  }
  return (
    <p className={styles.now} aria-live="polite">
      <span className={styles.nowText}>{text}</span>
      {age === null ? null : <span className={styles.age}>{translate('FlowAgo', { age: describeAge(age) })}</span>}
      {quiet && age !== null ? (
        <Label kind={kinds.DANGER}>{translate('FlowQuiet', { age: describeAge(age) })}</Label>
      ) : null}
    </p>
  );
}

function Details({ missions }: Readonly<{ missions: MissionView[] }>) {
  return (
    <details className={styles.details}>
      <summary>{translate('FlowDetails', { count: missions.length })}</summary>
      <ul className={styles.missions}>
        {missions.map((mission) => (
          <li key={mission.id} className={styles.mission}>
            <p className={styles.meta}>
              <strong>{describeMissionWorld(mission.world)}</strong> · {describeMissionState(mission.state)}
              {mission.end ? ` · ${describeMissionEnd(mission.end)}` : ''}
              {mission.mandate
                ? ` · ${translate('MissionMandate', { runner: mission.mandate.runnerId, acts: mission.mandate.acts.join(', ') || '-' })}`
                : ''}
            </p>
            {mission.dossier ? <p className={styles.claim}>{mission.dossier.claim}</p> : null}
            {mission.endReason ? <p className={styles.claim}>{mission.endReason}</p> : null}
            {mission.progress && mission.progress.length > 0 ? (
              <pre className={styles.progress}>{mission.progress.join('\n')}</pre>
            ) : null}
          </li>
        ))}
      </ul>
    </details>
  );
}

// One problem, followed: what it is, where it stands in each world, what it does now or what it waits for (the maintainer, 2026-11-25)
function Problem({ problem, missions, now }: Readonly<{ problem: FlowProblem; missions: MissionView[]; now: number }>) {
  // The frame and the label say what the problem does now: at work, judged, or waiting (dashed), and for what (ADR 0056)
  const operation = problem.state === 'queued' || problem.state === 'inProgress' ? problem.operation : null;
  const look = operation ? operationLook(operation) : null;
  return (
    <li className={classNames(styles.problem, look && frameOf(look))}>
      <div className={styles.head}>
        <span className={styles.title} title={problem.title}>
          {problem.title}
        </span>
        <span className={styles.repository}>{problem.repository}</span>
        {look ? (
          <Label kind={look.kind}>
            <look.Icon size={iconSizes.SMALL} aria-hidden="true" />
            {look.text}
          </Label>
        ) : (
          <Label kind={stateKinds[problem.state]}>{describeFlowState(problem.state)}</Label>
        )}
        {problem.priority >= urgentFrom ? (
          <Label kind={kinds.DANGER}>{translate('FlowUrgent', { priority: problem.priority })}</Label>
        ) : null}
      </div>
      <ol className={styles.steps} aria-label={translate('FlowSteps')}>
        {problem.steps.map((step) => (
          <li key={step.world} className={classNames(styles.step, stepStyles[step.state])}>
            <span className={styles.world}>{describeMissionWorld(step.world)}</span>
            <span className={styles.stepState}>
              {describeFlowStep(step.state)}
              {step.attempts > 1 ? ` · ${translate('FlowStepAttempts', { count: step.attempts })}` : ''}
            </span>
          </li>
        ))}
      </ol>
      {problem.activity ? <ActivityLine activity={problem.activity} now={now} /> : null}
      {problem.note ? <p className={styles.meta}>{problem.note}</p> : null}
      {missions.length > 0 ? <Details missions={missions} /> : null}
    </li>
  );
}

// The frame of a card: at work in the colour of what it does, waiting dashed (in the colour of a bot or of a missing runner when that is why)
function frameOf(look: { kind: string; waiting: boolean }): string | undefined {
  if (look.kind === kinds.PINK) {
    return classNames(styles.frameWaiting, styles.framePink);
  }
  if (look.kind === kinds.DANGER) {
    return classNames(look.waiting && styles.frameWaiting, styles.frameDanger);
  }
  if (look.waiting) {
    return styles.frameWaiting;
  }
  return look.kind === kinds.PURPLE ? styles.framePurple : styles.frameInfo;
}

// The same row ticked again later is the same problem: the missions of an earlier run are not the ones it is doing now
function ofThisRun(mission: MissionView, problem: FlowProblem): boolean {
  return (
    mission.problemId === problem.problemId &&
    (!mission.mandate || Date.parse(mission.mandate.issuedAt) >= Date.parse(problem.startedAt))
  );
}

export default function Flow({ problems, missions }: Readonly<{ problems: FlowProblem[]; missions: MissionView[] }>) {
  const now = useNow();
  if (problems.length === 0) {
    return <CapabilityHint>{translate('FlowEmpty')}</CapabilityHint>;
  }
  return (
    <ul className={styles.list} aria-label={translate('Missions')}>
      {problems.map((problem) => (
        <Problem
          key={problem.problemId}
          problem={problem}
          missions={missions.filter((mission) => ofThisRun(mission, problem))}
          now={now}
        />
      ))}
    </ul>
  );
}
