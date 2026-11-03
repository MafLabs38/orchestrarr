import { useApprovals } from 'Approvals/useApprovals';
import useWorkItems from 'Decisions/useWorkItems';
import * as icons from 'Helpers/Props/icons';
import { iconSizes } from 'Helpers/Props/sizes';
import translate from 'Utilities/translate';

import styles from './PageSidebar.module.css';
import SidebarLink from './SidebarLink';

export default function PageSidebar() {
  const { data } = useWorkItems();
  const { data: approvals } = useApprovals();
  return (
    <nav className={styles.sidebar} aria-label={translate('MainNavigation')}>
      <SidebarLink
        to="/"
        end
        icon={<icons.DECISIONS size={iconSizes.LARGE} aria-hidden="true" />}
        label={translate('Decisions')}
        badge={data?.items.length}
      />
      <SidebarLink
        to="/queue"
        icon={<icons.QUEUE size={iconSizes.LARGE} aria-hidden="true" />}
        label={translate('Queue')}
        badge={approvals?.length}
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
    </nav>
  );
}
