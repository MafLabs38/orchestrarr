import { useMutation, useQueryClient } from '@tanstack/react-query';

import sendJson from 'Helpers/Hooks/sendJson';
import useApiQuery from 'Helpers/Hooks/useApiQuery';
import type { OptionState } from 'typings/api';

export const optionsPath = '/api/v1/options';

export function useOptions() {
  return useApiQuery<OptionState[]>(optionsPath);
}

// Turning an option off lowers security and asks for the password of the person; turning it on asks for nothing.
export function useSetOption() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (change: { id: string; value: boolean; password?: string }) =>
      sendJson<OptionState[]>('PUT', `${optionsPath}/${change.id}`, { value: change.value, password: change.password }),
    // The status (does approving ask for a code?) and the cards change with it
    onSettled: () => queryClient.invalidateQueries(),
  });
}
