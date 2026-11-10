# 0023 — Options that lower security exist for development, only where the host allows it, and the executor keeps its own say

- **Status**: proposed (asked for by the maintainer on 2026-11-10; accepted when the pull request that builds it is merged)
- **Date**: 2026-11-10
- **Spec**: D-71 (refines D-65 and ADR 0017 for development)

## Context

The authenticator code of ADR 0017 is checked by the executor and cannot be skipped. That is the point, and it makes development and tests hard: nobody can try the screens (the table, the batches) without an authenticator enrolled in a terminal. The maintainer asked for an **options** screen where such things can be switched for development and tests, with the code as the first of them, and asked that the switch cannot be bypassed once the code is on.

An option that can be turned off from a screen is a hole by construction: a stolen session, a mistake or a taken-over control plane would use it. The design is therefore about **where the decision to relax is taken**, not about the switch.

## Decision

1. **A screen of options and an API for them** (`GET /api/v1/options`, `PUT /api/v1/options/{id}`). Everybody who signs in reads them. Changing one needs the permission `options.manage` (the `Admin` role).
2. **The host decides whether security may be lowered at all, when it starts.** `Options:AllowRelaxingSecurity` is `false` by default, is read once, and nothing the screen does can change it. The host **refuses to start in Production with it on**. With it off, an option that lowers security cannot be turned off from the screen, whoever asks, and the value stored for it is not even read (a value written by other means changes nothing). An unreadable store reads as the safe value.
3. **Turning one off needs the password of the administrator again**, is traced **before** it is made (a relaxation nobody can see is not made) as `option.changed`, and turning it on needs nothing. A banner on every screen says when the code is off.
4. **`Tasks:RequireSecondFactor=false` in the configuration still fixes the option off** and the screen cannot change it in either direction (it is refused in Production as before).
5. **The executor keeps its own say.** The control plane cannot tell the executor to skip the code (a taken-over control plane would then cancel the protection of ADR 0017). The executor has its own setting, `Executor:RequireSecondFactor` (true by default), set by whoever runs it. It can be false **only if every runner is a dry run**: the executor refuses to start otherwise (a runner is taken for a real one unless it says it is a dry run). The executor reports the setting in its hello and the status screen shows it. Both sides therefore have to be relaxed to approve without a code; one side alone produces a visible refusal in the run, not a silent bypass.

## Consequences

- Development gets a switch that production cannot have: the host that runs in Production cannot even be started with the leave, and an executor with a real runner cannot start without checking the code.
- The first real runner is the moment the setting stops being usable on an executor: that follows from the rule, and needs no new code then.
- The option is the first of a set; others (for example sample data, or a verbose trace) join the same screen with their own rules.
- The password is asked on the turn-off only; a stolen cookie of an administrator on a host that allows relaxing can still turn the code off if the thief also knows the password. The leave of the host is the real barrier, and a host that gives it is a development host.
