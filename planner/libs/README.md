# Vendored Calcite (the 1.43 trial's pin)

These are the three artefacts `planner/build.gradle.kts` pins — `calcite-core
1.43.0-20260915.055006-238`, `calcite-linq4j 1.43.0-20260915.055006-247` and `calcite-babel
1.43.0-20260915.055006-247` — with their POMs, in Maven's repository layout (a unique snapshot lives
in its `1.43.0-SNAPSHOT` directory under its timestamped file name) so `settings.gradle.kts` can serve
them as the `vendoredCalcite` repository, first, and the pin resolves from the tree on a cold cache
and in CI.

## Why this build, and why vendored

Apache's snapshot repository keeps only the **last sixteen deploys** of a module. It pruned the
trial's build (`…-20260909.084119-222`/`-231`) on 2026-09-15, five days after the pin;
`docs/design/calcite-143-trial.md` §7 named that as the failure mode to want and §8 records its
arrival. Those two artefacts were vendored the same day from this machine's Gradle cache, the only
surviving copies.

**`calcite-babel` never existed at the trial's timestamp**, which is what forced the re-pin. D259
needs the Babel parser, babel deploys in lock-step with `linq4j`'s build number, and every
2026-09-09 artefact was gone from the repository — so there was no coordinate to pin. The oldest
timestamp that still served all three on 2026-09-15 was `20260910.104401`; the newest, and the one
pinned here, is **`20260915.055006`**, chosen because a re-pin against this repository decays within
about a week either way and the newest build carries the most upstream fixes. `docs/design/calcite-143-trial.md`
§9 records what moved.

Provenance: downloaded on **2026-09-15** from
`https://repository.apache.org/content/groups/snapshots/org/apache/calcite/`, each file at the exact
path its coordinate names, after a `HEAD` on all six returned `200`. Two of the six are byte-identical
to the artefacts they replace — `calcite-linq4j`'s jar and POM did not change between the two
deploys, and neither did `calcite-core`'s POM — so the re-pin moves exactly one jar,
`calcite-core`'s.

`shasum -a 256 -c` over this list checks the directory:

```
9a68b27608401ff4cd3bc61fe3f4ae275c6a68ac15147e6378d8660f24afa5b1  ./org/apache/calcite/calcite-babel/1.43.0-SNAPSHOT/calcite-babel-1.43.0-20260915.055006-247.jar
03e83bc16878250a975353cbf3e463edfaf7f825b100ad07750ec62b08c6f0c9  ./org/apache/calcite/calcite-babel/1.43.0-SNAPSHOT/calcite-babel-1.43.0-20260915.055006-247.pom
11dc2358115bd8039225b5d76367a665198f74c350559db22361b68bb7a21c8c  ./org/apache/calcite/calcite-core/1.43.0-SNAPSHOT/calcite-core-1.43.0-20260915.055006-238.jar
f91036ce865c36aa2e5236299d29d457d3fc6292a7482824d9f83cb39f54cd20  ./org/apache/calcite/calcite-core/1.43.0-SNAPSHOT/calcite-core-1.43.0-20260915.055006-238.pom
02df1557a88fda994d0b20dbc3cf315debbec89b1b646922222b6191cfc33fa6  ./org/apache/calcite/calcite-linq4j/1.43.0-SNAPSHOT/calcite-linq4j-1.43.0-20260915.055006-247.jar
5668d59bd16260bbcc47b300c3b305b868c57184d2c1ed5fb1a1ce354e0f063f  ./org/apache/calcite/calcite-linq4j/1.43.0-SNAPSHOT/calcite-linq4j-1.43.0-20260915.055006-247.pom
```

## Rules

- Delete this directory with the `vendoredCalcite` block in `settings.gradle.kts` when the trial ends
  — `1.43.0` released to Central (F37) — and never add a fourth artefact here without its own line
  above.
- Every future pin is vendored **the same day it is pinned**. A timestamped coordinate against this
  repository has a retention half-life of roughly a week, so a pin that is not vendored is a build
  that will stop resolving without anyone touching it.
- The POMs of all three ask for moving `1.43.0-SNAPSHOT` versions of each other (`calcite-core` for
  `calcite-linq4j`, `calcite-babel` for `calcite-core`), so `build.gradle.kts` forces all three
  pinned builds. Vendoring does not remove that need: a forced version is what keeps a resolution
  from drifting onto whatever the repository serves next.
