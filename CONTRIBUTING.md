# Contributing to Chalk

## Getting a working tree

```bash
./scripts/install-tools.sh   # macOS/Homebrew: JDK 21, Gradle, buf, protoc, grpc plugin
./scripts/build.sh           # gradle build -x test, then dotnet build
./scripts/test.sh            # planner tests, .NET unit tests, integration tests
./scripts/test.sh --no-integration   # skip anything that needs the sidecar jar
```

`scripts/java-home.sh` finds a JDK 21+ without you having to export `JAVA_HOME`;
every other script uses it.

**PostgreSQL binaries are optional locally.** With them installed, `PostgresFixture`
starts a private cluster under your temp directory for the length of one test run
and deletes it afterwards, which adds the conformance kit's third dialect and the
federation corpus's third source — a real network-attached dialect rather than the
two in-process engines. Without them the PostgreSQL cases skip with a line naming
what to install; CI sets `CHALK_TEST_POSTGRES_REQUIRED=1` so the leg cannot be lost
quietly there. `brew install postgresql@16` on macOS,
`sudo apt-get install postgresql-16` on Debian or Ubuntu; on Windows, install from
the EDB installer and point `CHALK_PG_BIN` at its `bin` directory.
`CHALK_TEST_POSTGRES` names an existing server instead and wins over all of that.

## The two toolchains

`proto/` is the single shared artifact. **Both** builds compile that directory —
`dotnet/src/Chalk.Ir` and `dotnet/src/Chalk.Client` through `Grpc.Tools`, and
`planner/` through the Gradle protobuf plugin. No generated code is checked in
, so there is nothing to drift. Changing a `.proto` file changes both sides
at once; `buf lint` and `buf breaking` guard the policy in CI.

Never change `proto/` without a recorded decision. The IR is the only contract
between planner and executor (invariant I2).

## Layout

| Path | |
|---|---|
| `dotnet/src/` | the six client packages; `Chalk.Execution` is entirely `internal` |
| `dotnet/tests/` | unit tests per package, `Chalk.Integration.Tests` (needs the jar), `Chalk.TestKit` |
| `dotnet/tools/Chalk.CorpusTool` | `record` / `verify` / `print` for the plan corpus |
| `planner/src/main/java/chalk/planner` | catalog assembly, pipeline, rels/rules, `RelToIr`, RPC |
| `corpus/` | queries, recorded plans, digests — shared by both test suites |
| `dotnet/samples/` | the README quickstart, runnable via `scripts/quickstart.sh` |

## Conventions

- **Commits** are conventional: `feat(planner): …`, `fix(execution): …`,
  `test(ir): …`, `docs: …`, `chore: …`. `main` is the integration branch.
- **No generated code, no jars, no binaries** except `corpus/plans/*.binpb`,
  which are small and reviewed through their `.json` twin.
- **Public .NET API** is sealed classes with `required`/`init` properties, or
  interfaces. No positional records: they cannot grow additively.
- **No licence headers** in source files; `LICENSE` and `NOTICE` cover it.
- **Java**: `chalk.planner.*` packages, no wildcard imports.

## Testing rules that are not negotiable

- **No timing assertions anywhere.** Structural assertions, counters and
  differential comparisons only. A shared CI box makes wall-clock noise.
- **No per-row allocation** in the scan/filter/project path. `Chalk.Benchmarks`
  reports `Allocated / RowsScanned`; it must stay well under a byte.
- **No `Task.Result` / `.Wait()`** in engine code.
- **No FluentAssertions** (licence). Plain `Assert`, and AssertJ on the Java side.
- **No test may need the network** beyond a package restore.
- The reference executor (`ExecutionOptions.Engine = Reference`) is the
  correctness oracle; every corpus query is run through both engines and
  compared. If you add an operator, add its differential coverage.

## Changing a plan

Planning changes show up as a diff in `corpus/plans/`:

```bash
./scripts/record-plans.sh    # starts a sidecar, re-records every corpus plan
git diff corpus/plans        # the review signal
```

A changed `plan_digest` in a pull request means a rule or cost change altered
planning behaviour. That is the point; explain it in the description.

## Adding a corpus query

1. `corpus/queries/m1/<nn>_<name>.sql`, with a leading `-- expect:` block
   (`has(Kind)`, `not(Kind)`, `count(Kind)=n`, `param_types=[…]`,
   `root_collation=[…]`, `read_projection=n`, `has_function(ID)`, …).
2. If it uses `@name` or `$n` parameters, add its planner-visible twin under
   `corpus/queries/m1-rewritten/`; Calcite's parser knows only `?` (V13) and
   `ParameterRewriterTests` asserts the rewriter still produces exactly that text.
3. If it takes parameter values, add them to `DifferentialRunner.BindingsFor`.
4. `./scripts/record-plans.sh` to record its plan and digest.
5. It is picked up automatically by `GoldenPlanTests`, `CorpusDifferentialTests`
   and the planner's `PipelineTest`, `RelToIrTest` and `DigestTest`.

Two column names are traps: Calcite reserves `OPEN` and `CLOSE`, so write
`"open"` and `"close"`.

## Decision records

Anything that changes a contract — the IR, a capability, an entitlement rule — is
recorded with its context, decision and consequences. Never diverge silently.
