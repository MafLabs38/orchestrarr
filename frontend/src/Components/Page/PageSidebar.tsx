import useSession, { accountsManage, can } from 'Account/useSession';
import { useApprovals } from 'Approvals/useApprovals';
import useProblems from 'Decisions/useProblems';
import * as icons from 'Helpers/Props/icons';
import { iconSizes } from 'Helpers/Props/sizes';
import translate from 'Utilities/translate';

import styles from './PageSidebar.module.css';
import SidebarLink from './SidebarLink';

export default function PageSidebar() {
  const { data } = useProblems();
  const { data: approvals } = useApprovals();
  const { data: session } = useSession();
  return (
    <nav className={styles.sidebar} aria-label={translate('MainNavigation')}>
      <SidebarLink
        to="/"
        end
        icon={<icons.DECISIONS size={iconSizes.LARGE} aria-hidden="true" />}
        label={translate('Decisions')}
        badge={data?.rows.length}
      />
      <SidebarLink
        to="/history"
        icon={<icons.HISTORY size={iconSizes.LARGE} aria-hidden="true" />}
        label={translate('History')}
      />
      <SidebarLink
        to="/queue"
        icon={<icons.QUEUE size={iconSizes.LARGE} aria-hidden="true" />}
        label={translate('Queue')}
        badge={approvals?.length}
      />
      <SidebarLink
        to="/runs"
        icon={<icons.RUNS size={iconSizes.LARGE} aria-hidden="true" />}
        label={translate('Runs')}
      />
      <div className={styles.group}>{translate('System')}</div>
      <SidebarLink
        to="/system/status"
        icon={<icons.STATUS size={iconSizes.LARGE} aria-hidden="true" />}
        label={translate('Status')}
      />
      <SidebarLink
        to="/system/activity"
        icon={<icons.ACTIVITY size={iconSizes.LARGE} aria-hidden="true" />}
        label={translate('Activity')}
      />
      <SidebarLink
        to="/system/options"
        icon={<icons.OPTIONS size={iconSizes.LARGE} aria-hidden="true" />}
        label={translate('Options')}
      />
      {can(session, accountsManage) ? (
        <SidebarLink
          to="/system/accounts"
          icon={<icons.ACCOUNTS size={iconSizes.LARGE} aria-hidden="true" />}
          label={translate('Accounts')}
        />
      ) : null}
    </nav>
  );
}
