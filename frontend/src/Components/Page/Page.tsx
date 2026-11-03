import type { ReactNode } from 'react';

import translate from 'Utilities/translate';

import AccountRefused from './AccountRefused';
import styles from './Page.module.css';
import PageHeader from './PageHeader';
import PageSidebar from './PageSidebar';
import SignInFailed from './SignInFailed';

export default function Page({ children }: Readonly<{ children: ReactNode }>) {
  return (
    <div className={styles.page}>
      <a href="#main" className={styles.skip}>
        {translate('SkipToContent')}
      </a>
      <PageHeader />
      <div className={styles.body}>
        <PageSidebar />
        <main id="main" className={styles.main}>
          <SignInFailed />
          <AccountRefused />
          {children}
        </main>
      </div>
    </div>
  );
}
