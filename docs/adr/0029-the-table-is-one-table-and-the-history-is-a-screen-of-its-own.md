# 0029 — The table is one table, and the history is a screen of its own

- **Status**: accepted by the maintainer on 2026-11-12 (he asked for it)
- **Date**: 2026-11-12
- **Spec**: D-75 (amended), D-82; supersedes decision 1 of [ADR 0027](0027-the-table-shows-what-can-be-decided-by-application-and-keeps-what-was-decided.md)

## Context

[ADR 0027](0027-the-table-shows-what-can-be-decided-by-application-and-keeps-what-was-decided.md) put the rows of the table in three tabs (*To decide*, *To configure*, *To know*) beside a fourth for the history. The maintainer had asked that the history not be the default view, for a skip button, and for the table to show what is to be treated. The three tabs were the author's own cutting of the data: they were never asked for, never presented as a choice, and they hid most of the rows from the default view (one row in *To decide* against a hundred in the others, because Orchestrarr holds no write power yet). The columns of the table, and their names, had been settled before and are not changed.

## Decision

1. **There is one table**, with all the rows and the columns that were settled (No., Application, Type, Problem, CI, Runner, Plan, Why, Prod, and Skip for an operator). The filters are the ones of the table. The server gives no `tab`, and the sidebar counts the rows of the table.
2. **The history is a screen of its own** (*History*, in the sidebar like the queue and the activity), not a tab and not the default view: the rows approved, with what became of them, and the rows set aside, which can be taken back.
3. **What goes wrong with the description or its code goes in the notification bell** and nowhere on the screens: a repository that cannot be read, a path that matches nothing, an environment the fleet does not know, a failed reading (the status of the host carries them). The bell is the one place for notices.
4. **The rule of the project** (CLAUDE.md, rule 16): no product or interface design that was not asked for; a choice that changes what the maintainer sees is asked first and written as a choice in the description of the pull request.

## Consequences

- The first screen shows what it showed before, plus the application name and icon, the skip and the marks that were asked for.
- The rows are not cut by "can be decided". A danger is a row like the others.
