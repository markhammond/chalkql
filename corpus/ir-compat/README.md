# IR compatibility corpus

One directory per released IR version — `v1/`, `v2/`, … — holding a frozen copy of
`corpus/plans/` as it stood at that release. `Chalk.Ir.Tests.IrCompatReplayTests` parses,
validates, prints and digests every plan here on each build, and `Chalk.Execution.Tests`
executes the ones whose fixtures still exist.

This is how the compatibility policy in `docs/design/02-ir.md` §2 is *tested* rather than
asserted: a client at IR version N must keep reading plans recorded at N−k.

Empty until the first tagged release (`v0.1.0`); before that the proto files may change
in any way, field numbers included.

## `v<N>/entitled/`

Beside the frozen milestone plans, one subdirectory holds plans of an **entitled** catalog, recorded
by `IrCompatEntitledTests`. They are replayed twice: structurally by `IrCompatReplayTests`, like every
other recording, and again with the catalog they were planned against, which is what makes the
client-side entitlement invariant I-IR-E run over them (`docs/design/16-entitlements.md` §3.10). The
invariant is a promise about the plans this client accepts, and a promise nobody replays is a comment.

Regenerate with `CHALK_WRITE_FIXTURES=1` and read the diff.
