import useApiQuery from 'Helpers/Hooks/useApiQuery';
import type { Session } from 'typings/api';

export const sessionPath = '/auth/me';

// Who is signed in. It only answers when people sign in through the identity provider: anywhere else the route does not exist (a 404, asked
// once per page), and there is no session to show.
export default function useSession() {
  return useApiQuery<Session>(sessionPath, { once: true });
}
