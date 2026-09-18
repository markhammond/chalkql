rootProject.name = "chalk-planner"

dependencyResolutionManagement {
    repositories {
        mavenCentral()
        // The Calcite 1.43 trial (docs/design/calcite-143-trial.md). 1.43.0 is not released, and a
        // pinned build survives in Apache's snapshot repository for about a week: it keeps the last
        // sixteen deploys of a module and pruned the trial's 2026-09-09 build five days after it was
        // pinned. So the three jars and their POMs are vendored under planner/libs in Maven's layout
        // (a unique snapshot lives in its `1.43.0-SNAPSHOT` directory under its timestamped file
        // name), and this repository comes first so the pin resolves from the tree on a cold cache
        // and in CI. planner/libs/README.md carries the provenance and the SHA-256 of every file.
        // Delete this block, and the directory, when the trial ends — 1.43.0 released to Central.
        maven {
            name = "vendoredCalcite"
            url = uri(rootDir.resolve("libs"))
            content {
                includeModule("org.apache.calcite", "calcite-core")
                includeModule("org.apache.calcite", "calcite-linq4j")
                includeModule("org.apache.calcite", "calcite-babel")
            }
        }
        // Kept for the next pin: the snapshot repository serves only the Calcite modules, by name, so
        // nothing else in the build can drift onto an unreleased artefact. It is not where the pin
        // comes from — vendoredCalcite above is — and a build that reaches this far for a Calcite
        // module is a build whose vendored copy is missing.
        maven {
            name = "apacheSnapshots"
            url = uri("https://repository.apache.org/content/groups/snapshots/")
            content {
                includeModule("org.apache.calcite", "calcite-core")
                includeModule("org.apache.calcite", "calcite-linq4j")
                includeModule("org.apache.calcite", "calcite-babel")
            }
        }
    }
}
