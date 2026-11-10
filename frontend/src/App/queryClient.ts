import { QueryCache, QueryClient } from '@tanstack/react-query';

import { sessionPath } from 'Account/useSession';
import { ApiError } from 'Helpers/Hooks/useApiQuery';

const queryClient: QueryClient = new QueryClient({
  // An answer of 401 to anything but the question "who am I" means the session ended: ask again, and the interface shows the sign-in
  queryCache: new QueryCache({
    onError: (error, query) => {
      if (error instanceof ApiError && error.status === 401 && query.queryKey[0] !== sessionPath) {
        void queryClient.invalidateQueries({ queryKey: [sessionPath] });
      }
    },
  }),
  defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: true, staleTime: 5_000 } },
});

export default queryClient;
