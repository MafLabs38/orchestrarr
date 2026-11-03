import useWorkItems from 'Decisions/useWorkItems';
import * as icons from 'Helpers/Props/icons';
import { iconSizes } from 'Helpers/Props/sizes';
import translate from 'Utilities/translate';

import styles from './PageSidebar.module.css';
import SidebarLink from './SidebarLink';

export default function PageSidebar() {
  const { data } = useWorkItems();
  return (
    <nav className={styles.sidebar} aria-label={translate('MainNavigation')}>
      <SidebarLink
        to="/"
        end
        icon={<icons.DECISIONS size={iconSizes.LARGE} aria-hidden="true" />}
        label={translate('Decisions')}
        badge={data?.items.length}
      />
      <div className={styles.group}>{translate('System')}</div>
      <SidebarLink
        to="/system/status"
        icon={<icons.STATUS size={iconSizes.LARGE} aria-hidden="true" />}
        label={translate('Status')}
      />
    </nav>
  );
}
