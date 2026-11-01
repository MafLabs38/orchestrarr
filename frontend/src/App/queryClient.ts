import { QueryClient } from '@tanstack/react-query';

export default new QueryClient({
  defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: true, staleTime: 5_000 } },
});
