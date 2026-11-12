import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';

import sendJson from 'Helpers/Hooks/sendJson';
import useApiQuery, { getJson, refreshIntervalMs } from 'Helpers/Hooks/useApiQuery';
import { systemStatusPath } from 'System/useSystemStatus';
import type { Approval, Ceiling, HistoryReport, HistoryState, ProblemRow, ProblemsReport } from 'typings/api';

export const problemsPath = '/api/v1/problems';
export const historyPath = '/api/v1/problems/history';
const approvalsPath = '/api/v1/approvals';
const auditPath = '/api/v1/audit';

// While the pull requests of some application are still being read, read again soon to get them
const pendingIntervalMs = 3000;

export function intervalFor(report: ProblemsReport | undefined): number {
  return report?.rows.some((row) => row.kind === 'pullRequestsNotRead') ? pendingIntervalMs : refreshIntervalMs;
}

export default function useProblems() {
  return useApiQuery<ProblemsReport>(problemsPath, { refetchInterval: intervalFor });
}

// Reads the source again now, instead of waiting for the cache (the server still spaces its reads, to protect the source)
export function useRefreshProblems() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => getJson<ProblemsReport>(`${problemsPath}?refresh=true`),
    onSuccess: (report) => {
      queryClient.setQueryData([problemsPath], report);
      return queryClient.invalidateQueries({ queryKey: [systemStatusPath] });
    },
  });
}

// What the operator saw: the commit and how far the row went. The server refuses the selection when either has moved since.
export interface SelectedRow {
  id: string;
  headSha: string;
  goesUpTo: Ceiling;
  stopBeforeProduction: boolean;
}

// Approves the selection with one code. The client says which rows it selected, at which commit, and the switches; the server computes the
// plan again from the current table, so nothing sent here can widen what a row may do.
export function useApproveProblems() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: { rows: SelectedRow[]; secondFactorCode?: string; descriptionsVersion: string }) =>
      sendJson<{ approvals: Approval[] }>('POST', `${problemsPath}/approve`, request),
    onSettled: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: [problemsPath] }),
        queryClient.invalidateQueries({ queryKey: [approvalsPath] }),
        queryClient.invalidateQueries({ queryKey: [auditPath] }),
        queryClient.invalidateQueries({ queryKey: [systemStatusPath] }),
      ]),
  });
}

// Sets rows aside: what the operator saw is sent back (the identifier and the description), and the server refuses a row that has changed since.
export function useSkipProblems() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (rows: ProblemRow[]) =>
      sendJson<{ skipped: number }>('POST', `${problemsPath}/skip`, {
        rows: rows.map((row) => ({ id: row.id, description: row.description })),
      }),
    onSettled: () =>
      queryClient.invalidateQueries({ predicate: ({ queryKey }) => String(queryKey[0]).startsWith(problemsPath) }),
  });
}

export function useUnskip() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => sendJson<undefined>('POST', `${problemsPath}/unskip`, { id }),
    onSettled: () =>
      queryClient.invalidateQueries({ predicate: ({ queryKey }) => String(queryKey[0]).startsWith(problemsPath) }),
  });
}

// The history, a page at a time: the oldest lines are read only when asked for
export function useHistory(state: HistoryState | null) {
  return useInfiniteQuery({
    queryKey: [historyPath, state],
    queryFn: ({ pageParam }) => {
      const query = new URLSearchParams();
      if (state) {
        query.set('state', state);
      }
      if (pageParam !== null) {
        query.set('before', String(pageParam));
      }
      const text = query.toString();
      return getJson<HistoryReport>(text ? `${historyPath}?${text}` : historyPath);
    },
    initialPageParam: null as number | null,
    getNextPageParam: (last) => last.next,
  });
}
