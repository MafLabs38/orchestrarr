import type { ReactNode } from 'react';
import translate from 'Utilities/translate';
import PageHeader from './PageHeader';
import PageSidebar from './PageSidebar';
import styles from './Page.module.css';

export default function Page({ children }: { children: ReactNode }) {
  return (
    <div className={styles.page}>
      <a href="#main" className={styles.skip}>
        {translate('SkipToContent')}
      </a>
      <PageHeader />
      <div className={styles.body}>
        <PageSidebar />
        <main id="main" className={styles.main}>
          {children}
        </main>
      </div>
    </div>
  );
}
