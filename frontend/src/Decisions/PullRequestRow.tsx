import Label from 'Components/Label';
import * as icons from 'Helpers/Props/icons';
import { type Kind, kinds } from 'Helpers/Props/kinds';
import { iconSizes } from 'Helpers/Props/sizes';
import type { ChecksState, PullRequest, UpdateKind } from 'typings/api';
import { describeChanges, describeChecks, describeMerge, describeUpdateKind, updateKindOf } from 'Utilities/describe';
import translate from 'Utilities/translate';

import styles from './PullRequestRow.module.css';

const checkPresentation = {
  passing: { kind: kinds.SUCCESS, Icon: icons.SUCCESS },
  failing: { kind: kinds.DANGER, Icon: icons.DANGER },
  pending: { kind: kinds.INFO, Icon: icons.PENDING },
  unknown: { kind: kinds.DISABLED, Icon: icons.UNKNOWN },
} satisfies Record<ChecksState, { kind: Kind; Icon: unknown }>;

// A bigger jump is a bigger risk: the word says it, the color only helps
const updateKinds: Record<UpdateKind, Kind> = {
  major: kinds.WARNING,
  minor: kinds.INFO,
  patch: kinds.DEFAULT,
  unknown: kinds.DEFAULT,
};

// Only a link to a web page is rendered as a link, whatever the data says
function safeHref(url: string): string | null {
  try {
    return new URL(url).protocol === 'https:' ? url : null;
  } catch {
    return null;
  }
}

// One pull request: what it updates, how far, whether its checks pass and whether it can be merged. It only shows, nothing here acts.
export default function PullRequestRow({ pullRequest }: Readonly<{ pullRequest: PullRequest }>) {
  const { Icon, kind: checksKind } = checkPresentation[pullRequest.checks];
  const href = safeHref(pullRequest.url);
  const updateKind = updateKindOf(pullRequest);
  const update = describeUpdateKind(updateKind);
  const number = `#${pullRequest.number}`;

  return (
    <li className={styles.row}>
      <div className={styles.main}>
        {href ? (
          <a
            href={href}
            rel="noreferrer noopener"
            target="_blank"
            aria-label={translate('PullRequestLink', { number: pullRequest.number })}
          >
            {number}
          </a>
        ) : (
          <span>{number}</span>
        )}
        <span className={styles.changes}>{describeChanges(pullRequest)}</span>
        {pullRequest.headSha ? <span className={styles.commit}>{pullRequest.headSha.slice(0, 7)}</span> : null}
        {update ? <Label kind={updateKinds[updateKind]}>{update}</Label> : null}
      </div>
      <div className={styles.state}>
        <Label kind={checksKind}>
          <Icon size={iconSizes.SMALL} aria-hidden="true" />
          {describeChecks(pullRequest.checks)}
        </Label>
        <span>{pullRequest.draft ? describeMerge('draft') : describeMerge(pullRequest.merge)}</span>
      </div>
    </li>
  );
}
