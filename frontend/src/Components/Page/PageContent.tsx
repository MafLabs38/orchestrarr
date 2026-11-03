import { type ReactNode, useEffect } from 'react';

import translate from 'Utilities/translate';

// Every page is a PageContent: it sets the browser tab title.
export default function PageContent({ title, children }: Readonly<{ title: string; children: ReactNode }>) {
  useEffect(() => {
    document.title = `${title} · ${translate('AppName')}`;
  }, [title]);
  return <>{children}</>;
}
