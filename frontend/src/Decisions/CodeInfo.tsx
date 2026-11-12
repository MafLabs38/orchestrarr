import InfoPopover from 'Components/InfoPopover';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import type { CodeChangeReport, CodeLocation, ProblemRow } from 'typings/api';
import translate from 'Utilities/translate';

import styles from './CodeInfo.module.css';
import { problemsPath } from './useProblems';

// The line that was removed, the one that was added: coloured, and not by colour alone (the sign is in the text)
function lineClass(line: string): string | undefined {
  if (line.startsWith('-') && !line.startsWith('---')) {
    return styles.removed;
  }
  return line.startsWith('+') && !line.startsWith('+++') ? styles.added : undefined;
}

function Place({ location }: Readonly<{ location: CodeLocation }>) {
  return (
    <li>
      <code>
        {location.repository}/{location.path}:{location.line}
      </code>
      {location.image.tag ? <span className={styles.tag}> {location.image.tag}</span> : null}
      {location.managed ? (
        <span className={styles.managed}> {translate('CodeFlux', { automation: location.managed })}</span>
      ) : null}
    </li>
  );
}

// Only mounted when the bubble is open: the change is computed on the server when it is asked for, and never applied
function Change({ id }: Readonly<{ id: string }>) {
  const { data, isPending, isError } = useApiQuery<CodeChangeReport>(
    `${problemsPath}/change?id=${encodeURIComponent(id)}`,
    { once: true },
  );
  if (isPending) {
    return <p className={styles.note}>{translate('Loading')}</p>;
  }
  if (isError || !data) {
    return <p className={styles.note}>{translate('CodeChangeUnavailable')}</p>;
  }
  return (
    <div className={styles.change}>
      <p className={styles.note}>{translate('CodeChangeFor', { tag: data.tag ?? '' })}</p>
      {data.changes.map((change) => (
        <pre key={`${change.repository}/${change.path}:${change.line}`} className={styles.diff}>
          {change.diff.split('\n').map((line, index) => (
            // The lines of a diff are in a fixed order and never reordered: the position is their identity
            <span key={index} className={lineClass(line)}>
              {line}
              {'\n'}
            </span>
          ))}
        </pre>
      ))}
      {data.left.length > 0 ? (
        <>
          <p className={styles.note}>{translate('CodeChangeLeft')}</p>
          <ul className={styles.places}>
            {data.left.map((location) => (
              <Place key={`${location.repository}/${location.path}:${location.line}`} location={location} />
            ))}
          </ul>
        </>
      ) : null}
      <p className={styles.note}>{translate('CodeNothingWritten')}</p>
    </div>
  );
}

// Where the version of a row is written in the code of the scope, and the change that would write the proposed one (ADR 0028)
export default function CodeInfo({ row, sentence }: Readonly<{ row: ProblemRow; sentence: string }>) {
  if (!row.locations || row.locations.length === 0) {
    return null;
  }
  return (
    <InfoPopover label={translate('CodeWhereOf', { problem: `${row.application} ${sentence}` })}>
      <strong>{translate('CodeWhere')}</strong>
      <ul className={styles.places}>
        {row.locations.map((location) => (
          <Place key={`${location.repository}/${location.path}:${location.line}`} location={location} />
        ))}
      </ul>
      {row.proposedTag ? <Change id={row.id} /> : null}
    </InfoPopover>
  );
}
