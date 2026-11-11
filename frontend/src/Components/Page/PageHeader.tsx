import BrandLogo from 'Components/BrandLogo';
import translate from 'Utilities/translate';

import LiveBadge from './LiveBadge';
import styles from './PageHeader.module.css';
import SignedInUser from './SignedInUser';
import SourceBadge from './SourceBadge';

export default function PageHeader() {
  return (
    <header className={styles.header}>
      <BrandLogo size={40} />
      <span className={styles.name}>{translate('AppName')}</span>
      <span className={styles.spacer} />
      <LiveBadge />
      <SourceBadge />
      <SignedInUser />
    </header>
  );
}
