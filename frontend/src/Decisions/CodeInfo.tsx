import InfoPopover from 'Components/InfoPopover';
import type { CodeLocation, ProblemRow } from 'typings/api';
import translate from 'Utilities/translate';

import styles from './CodeInfo.module.css';

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

// Where the version of a row is written in the code of the scope (ADR 0028)
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
    </InfoPopover>
  );
}

// What the bell says about a repository that cannot be read, said again on the rows it concerns: the repository and why, never "nothing found"
export function RowWarnings({ row, sentence }: Readonly<{ row: ProblemRow; sentence: string }>) {
  if (!row.warnings || row.warnings.length === 0) {
    return null;
  }
  return (
    <InfoPopover warning label={translate('CodeWarningOf', { problem: `${row.application} ${sentence}` })}>
      <strong>{translate('CodeWarning')}</strong>
      <ul className={styles.places}>
        {row.warnings.map((warning) => (
          <li key={`${warning.repository}:${warning.reason}`}>
            <code>{warning.repository}</code> {warning.reason}
          </li>
        ))}
      </ul>
    </InfoPopover>
  );
}
