import { useMutation, useQueryClient } from '@tanstack/react-query';

import sendJson from 'Helpers/Hooks/sendJson';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import type { Account, Role, Session } from 'typings/api';

import { sessionPath } from './useSession';

export const accountsPath = '/api/v1/accounts';
export const rolesPath = '/api/v1/roles';

// Signing in: the answer is who signed in, which is what the session query holds from then on, and everything else is read again
export function useSignIn() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (credentials: { name: string; password: string }) =>
      sendJson<Session>('POST', '/api/v1/auth/login', credentials),
    onSuccess: async (session) => {
      await queryClient.invalidateQueries({ predicate: (query) => query.queryKey[0] !== sessionPath });
      queryClient.setQueryData([sessionPath], session);
    },
  });
}

// Signing out ends the session on the server; what was read for this person is forgotten, and the session is asked again (a 401)
export function useSignOut() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => sendJson<undefined>('POST', '/api/v1/auth/logout'),
    onSettled: async () => {
      queryClient.removeQueries({ predicate: (query) => query.queryKey[0] !== sessionPath });
      await queryClient.invalidateQueries({ queryKey: [sessionPath] });
    },
  });
}

export function useChangePassword() {
  return useMutation({
    mutationFn: (request: { currentPassword: string; newPassword: string }) =>
      sendJson<undefined>('POST', '/api/v1/auth/password', request),
  });
}

export function useAccounts() {
  return useApiQuery<Account[]>(accountsPath);
}

export function useRoles() {
  return useApiQuery<Role[]>(rolesPath, { once: true });
}

export function useAccountChanges() {
  const queryClient = useQueryClient();
  const refresh = () => queryClient.invalidateQueries({ queryKey: [accountsPath] });
  return {
    create: useMutation({
      mutationFn: (account: { name: string; role: string; password: string }) =>
        sendJson<Account>('POST', accountsPath, account),
      onSuccess: refresh,
    }),
    update: useMutation({
      mutationFn: (change: { id: string; role: string; enabled: boolean }) =>
        sendJson<Account>('PUT', `${accountsPath}/${change.id}`, { role: change.role, enabled: change.enabled }),
      onSettled: refresh,
    }),
    setPassword: useMutation({
      mutationFn: (change: { id: string; password: string }) =>
        sendJson<undefined>('POST', `${accountsPath}/${change.id}/password`, { password: change.password }),
    }),
    remove: useMutation({
      mutationFn: (id: string) => sendJson<undefined>('DELETE', `${accountsPath}/${id}`),
      onSuccess: refresh,
    }),
  };
}
