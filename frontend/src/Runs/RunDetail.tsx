import classNames from 'classnames';
import { Link, useParams } from 'react-router-dom';

import Alert from 'Components/Alert';
import CapabilityHint from 'Components/CapabilityHint';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import PageHeading from 'Components/Page/PageHeading';
import { kinds } from 'Helpers/Props/kinds';
import type { RunDetail as RunDetailResource, RunEvent, RunTask } from 'typings/api';
import { describeRunEvent, describeRunRefusal, describeTaskKind, describeTaskResult } from 'Utilities/describe';
import formatTime from 'Utilities/formatTime';
import translate from 'Utilities/translate';

import styles from './Runs.module.css';
import StateLabel from './StateLabel';
import { useRun } from './useRuns';

function TaskItem({ task }: Readonly<{ task: RunTask }>) {
  const { spec } = task;
  return (
    <li className={styles.task}>
      <div>
        {translate('TaskLine', {
          kind: describeTaskKind(spec.kind),
          repository: spec.repository,
          number: spec.pullRequest,
          commit: spec.headSha.slice(0, 7),
        })}{' '}
        <strong>{describeTaskResult(task.result)}</strong>
      </div>
      {task.reason ? <div className={styles.reason}>{task.reason}</div> : null}
    </li>
  );
}

// The detail of a line. A patch, and the change an agent makes to a file, are shown as a diff: what is removed in red, what is added in green.
function EventDetail({ event }: Readonly<{ event: RunEvent }>) {
  const detail = event.detail ?? '';
  const isDiff = event.type === 'task.patch' || (event.type === 'task.agent' && detail.startsWith('edits '));
  if (!isDiff) {
    return <>{detail}</>;
  }
  return (
    <>
      {detail.split('\n').map((line, index) => (
        // The lines of a text have no identity of their own: their place is their key
        <span key={`${index}:${line}`} className={diffClass(line)}>
          {line}
          {'\n'}
        </span>
      ))}
    </>
  );
}

function diffClass(line: string): string | undefined {
  if (line.startsWith('+') && !line.startsWith('+++')) {
    return styles.added;
  }
  if (line.startsWith('-') && !line.startsWith('---')) {
    return styles.removed;
  }
  return line.startsWith('@@') ? styles.hunk : undefined;
}

// A line of the trace: when, what the executor was doing, and what it found. The short hash is the link of the chain.
function EventItem({ event }: Readonly<{ event: RunEvent }>) {
  return (
    <li className={classNames(styles.event, event.type === 'task.agent' && styles.agentEvent)}>
      <span className={styles.mono}>{formatTime(event.at)}</span>
      <span className={styles.eventType}>{describeRunEvent(event.type)}</span>
      <span className={styles.eventDetail}>
        <EventDetail event={event} />
      </span>
      <span className={styles.hash} title={event.hash}>
        {event.hash.slice(0, 8)}
      </span>
    </li>
  );
}

function Body({ run }: Readonly<{ run: RunDetailResource }>) {
  const { summary } = run;
  return (
    <>
      <p className={styles.summary}>
        <StateLabel state={summary.state} />{' '}
        {summary.refusal ? <strong>{describeRunRefusal(summary.refusal)}</strong> : null}
      </p>
      <p className={styles.meta}>
        {translate('RunWho', { approver: summary.approverSubject, time: formatTime(summary.createdAt) ?? '' })}
        {' · '}
        {summary.executor ? translate('RunExecutor', { executor: summary.executor }) : translate('RunNoExecutor')}
      </p>

      <h4 className={styles.section}>{translate('RunTasks')}</h4>
      <ul className={styles.tasks}>
        {run.tasks.map((task) => (
          <TaskItem key={task.spec.id} task={task} />
        ))}
      </ul>

      <h4 className={styles.section}>{translate('RunTrace')}</h4>
      <CapabilityHint>{translate('RunTraceChained')}</CapabilityHint>
      {run.events.length === 0 ? (
        <CapabilityHint>{translate('RunTraceEmpty')}</CapabilityHint>
      ) : (
        <ol className={styles.events}>
          {run.events.map((event) => (
            <EventItem key={event.sequence} event={event} />
          ))}
        </ol>
      )}
    </>
  );
}

// One run: its tasks, what became of each, and the chained trace of the executor, as a timeline.
export default function RunDetail() {
  const { id = '' } = useParams();
  const { data: run, error, isPending } = useRun(id);
  const notFound = error?.status === 404;

  return (
    <PageContent title={translate('Runs')}>
      <PageContentBody>
        <PageHeading
          scope={translate('Runs')}
          title={run ? run.summary.workItemId.replace(/^repository:/, '') : translate('Runs')}
        />
        <p>
          <Link to="/runs">{translate('RunBack')}</Link>
        </p>
        {isPending ? <CapabilityHint>{translate('Loading')}</CapabilityHint> : null}
        {error ? (
          <Alert kind={kinds.DANGER}>
            {notFound ? translate('RunNotFound') : translate('CannotReadRuns', { error: error.message })}
          </Alert>
        ) : null}
        {run ? <Body run={run} /> : null}
      </PageContentBody>
    </PageContent>
  );
}
