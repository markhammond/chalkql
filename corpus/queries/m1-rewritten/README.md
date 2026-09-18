# Planner-visible SQL

`corpus/queries/m1/*.sql` is the SQL a *host* writes. Calcite's parser knows only positional `?`
(V13), so `Chalk.Client.ParameterRewriter` rewrites `@name` and `$n` — and expands a list parameter
into an `IN (?, …)` list — before the planner ever sees the statement (D27, D29).

This directory holds that rewritten form for the queries where the two differ, in the shape
`PrepareAsync` plans: every list-capable parameter has exactly one element. Queries whose SQL is
already positional have no file here.

`Chalk.Integration.Tests.ParameterRewriterCorpusTests` asserts that the rewriter still produces
exactly these strings, so there is one source of truth (the rewriter) and the planner's Java tests
can plan the same text without reimplementing it.
