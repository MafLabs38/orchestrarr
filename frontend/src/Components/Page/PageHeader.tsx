import * as icons from 'Helpers/Props/icons';
import translate from 'Utilities/translate';

import styles from './PageHeader.module.css';
import SourceBadge from './SourceBadge';

export default function PageHeader() {
  return (
    <header className={styles.header}>
      <icons.LOGO className={styles.logo} size={26} strokeWidth={2.5} aria-hidden="true" />
      <span className={styles.name}>{translate('AppName')}</span>
      <span className={styles.spacer} />
      <SourceBadge />
    </header>
  );
}
