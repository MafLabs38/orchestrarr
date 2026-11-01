export const kinds = {
  DEFAULT: 'default',
  PRIMARY: 'primary',
  SUCCESS: 'success',
  WARNING: 'warning',
  DANGER: 'danger',
  INFO: 'info',
  INVERSE: 'inverse',
  DISABLED: 'disabled',
} as const;

export type Kind = (typeof kinds)[keyof typeof kinds];
