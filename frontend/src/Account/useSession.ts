import useApiQuery from 'Helpers/Hooks/useApiQuery';
import type { Session } from 'typings/api';

export const sessionPath = '/api/v1/auth/me';

// Who is signed in. It is asked once per page: a 401 means nobody is, and the interface shows the sign-in (the query cache asks again when
// another answer says the session ended).
export default function useSession() {
  return useApiQuery<Session>(sessionPath, { once: true });
}

export const accountsManage = 'accounts.manage';
export const optionsManage = 'options.manage';

export function can(session: Session | undefined, permission: string): boolean {
  return session?.permissions.includes(permission) === true;
}
