import type { ReactNode } from 'react';

import SignIn from 'Account/SignIn';
import useSession from 'Account/useSession';
import Alert from 'Components/Alert';
import { ApiError } from 'Helpers/Hooks/useApiQuery';
import { kinds } from 'Helpers/Props/kinds';
import useSystemStatus from 'System/useSystemStatus';
import translate from 'Utilities/translate';

import styles from './Page.module.css';
import PageHeader from './PageHeader';
import PageSidebar from './PageSidebar';

// When approving asks for no code, every screen says so: it must not be forgotten that security is lowered (development and tests only)
function SecondFactorBanner() {
  const { data: status } = useSystemStatus();
  return status && !status.approvals.requiresSecondFactor ? (
    <Alert kind={kinds.WARNING}>{translate('SecondFactorOffBanner')}</Alert>
  ) : null;
}

// Nothing of the fleet is shown to somebody who has not signed in: the sign-in comes first, and the shell only exists for a session.
export default function Page({ children }: Readonly<{ children: ReactNode }>) {
  const session = useSession();
  if (session.isPending) {
    return null;
  }
  if (session.error instanceof ApiError && session.error.status === 401) {
    return <SignIn />;
  }
  if (session.isError) {
    return (
      <main className={styles.main}>
        <Alert kind={kinds.DANGER}>{translate('CannotReachServer')}</Alert>
      </main>
    );
  }

  return (
    <div className={styles.page}>
      <a href="#main" className={styles.skip}>
        {translate('SkipToContent')}
      </a>
      <PageHeader />
      <div className={styles.body}>
        <PageSidebar />
        <main id="main" className={styles.main}>
          <SecondFactorBanner />
          {children}
        </main>
      </div>
    </div>
  );
}
