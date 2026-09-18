import com.google.protobuf.gradle.id

plugins {
    java
    application
    id("com.google.protobuf") version "0.9.6"
    id("com.gradleup.shadow") version "8.3.9"
}

// Versions — 01-repo-and-toolchain.md §2, re-verified 2026-09-07 against Maven Central.
// Calcite is on trial (docs/design/calcite-143-trial.md): one *timestamped* 1.43.0 snapshot build,
// never the moving `1.43.0-SNAPSHOT`, so the build is reproducible and the jar cannot change under
// a cached checkout. All three artefacts are vendored under planner/libs (see settings.gradle.kts):
// Apache's snapshot repository keeps sixteen deploys and pruned the trial's build five days after
// the pin, which is also why the pin moved from 2026-09-09 to 2026-09-15 — `calcite-babel`, which
// D259 needs, never existed at the trial's timestamp (trial document §9).
//
// One deploy, three build numbers: the same 20260915.055006 deploy numbered core 238 and both
// linq4j and babel 247. Each is therefore pinned to its own timestamped coordinate, and all three
// are forced below, because every one of these POMs asks for a *moving* `1.43.0-SNAPSHOT` of the
// others — core for linq4j, babel for core.
val calciteVersion = "1.43.0-20260915.055006-238"
val calciteLinq4jVersion = "1.43.0-20260915.055006-247"
val calciteBabelVersion = "1.43.0-20260915.055006-247"
val grpcVersion = "1.84.0"
val protobufVersion = "4.36.1"
val slf4jVersion = "2.0.19"
val logbackVersion = "1.5.20"
val junitVersion = "5.14.4"
val assertjVersion = "3.27.7"

java {
    toolchain {
        languageVersion = JavaLanguageVersion.of(21)
    }
}

application {
    mainClass = "chalk.planner.Main"
}

dependencies {
    implementation("org.apache.calcite:calcite-core:$calciteVersion")
    implementation("org.apache.calcite:calcite-linq4j:$calciteLinq4jVersion")
    // D259: the Babel parser serves a request whose conformance is BABEL, and only that. It is one
    // parser factory on the SqlParser.Config; nothing else in the planner changes, and every other
    // conformance keeps the core parser.
    implementation("org.apache.calcite:calcite-babel:$calciteBabelVersion")

    implementation("com.google.protobuf:protobuf-java:$protobufVersion")
    implementation("com.google.protobuf:protobuf-java-util:$protobufVersion")
    implementation("io.grpc:grpc-protobuf:$grpcVersion")
    implementation("io.grpc:grpc-stub:$grpcVersion")
    implementation("io.grpc:grpc-services:$grpcVersion")
    // Not runtimeOnly: PlannerServer binds an explicit InetSocketAddress so --host actually
    // restricts the interface, and only NettyServerBuilder can take one.
    implementation("io.grpc:grpc-netty-shaded:$grpcVersion")

    // grpc-java's generated stubs reference javax.annotation.Generated.
    compileOnly("org.apache.tomcat:annotations-api:6.0.53")
    // Calcite's config interfaces are annotated with @Value.Immutable; without the annotations on
    // the compile classpath javac warns on every file that touches SqlValidator.Config.
    compileOnly("org.immutables:value-annotations:2.10.1")
    testCompileOnly("org.immutables:value-annotations:2.10.1")

    implementation("org.slf4j:slf4j-api:$slf4jVersion")
    runtimeOnly("ch.qos.logback:logback-classic:$logbackVersion")

    testImplementation(platform("org.junit:junit-bom:$junitVersion"))
    testImplementation("org.junit.jupiter:junit-jupiter")
    testImplementation("org.assertj:assertj-core:$assertjVersion")
    testImplementation("io.grpc:grpc-inprocess:$grpcVersion")
    testImplementation("io.grpc:grpc-testing:$grpcVersion")
    testImplementation("ch.qos.logback:logback-classic:$logbackVersion")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}

// Every one of these POMs asks for a *moving* `1.43.0-SNAPSHOT` of another: calcite-core for
// calcite-linq4j, and calcite-babel for calcite-core. Forcing all three pinned builds is what stops
// a resolution a week from now from picking up a different jar than the one this trial measured —
// and, with babel in the graph, what stops babel's own dependency from pulling a core that is not
// the pinned one. Vendoring does not make this unnecessary: the moving coordinate would otherwise
// resolve against the snapshot repository, which serves whatever it deployed last.
configurations.configureEach {
    resolutionStrategy.force(
        "org.apache.calcite:calcite-core:$calciteVersion",
        "org.apache.calcite:calcite-linq4j:$calciteLinq4jVersion",
        "org.apache.calcite:calcite-babel:$calciteBabelVersion",
    )
}

// THE shared contract: both builds compile proto/ directly. Nothing generated is checked in (D14).
sourceSets {
    main {
        proto {
            srcDir(layout.projectDirectory.dir("../proto"))
        }
    }
}

protobuf {
    protoc {
        artifact = "com.google.protobuf:protoc:$protobufVersion"
    }
    plugins {
        id("grpc") {
            artifact = "io.grpc:protoc-gen-grpc-java:$grpcVersion"
        }
    }
    generateProtoTasks {
        all().forEach { task ->
            task.plugins { id("grpc") }
        }
    }
}

tasks.withType<JavaCompile>().configureEach {
    options.encoding = "UTF-8"
    // D134: warnings are errors. -Werror is what keeps a deprecation like RelOptRule.operand from
    // going unnoticed for a milestone. The three exclusions predate it: -processing because no
    // annotation processor runs, -serial because nothing here is serialised, -this-escape because
    // Calcite's own rel constructors publish `this` to a cluster by design and every subclass
    // inherits the warning.
    options.compilerArgs.addAll(listOf("-Xlint:all,-processing,-serial,-this-escape", "-Werror"))
}

tasks.test {
    useJUnitPlatform()
    // PlannerConfig reads CHALK_PLANNER_* from the environment; a developer who has one exported
    // must not change what the transport tests parse.
    listOf("CHALK_PLANNER_HOST", "CHALK_PLANNER_PORT", "CHALK_PLANNER_SOCKET", "CHALK_PLANNER_THREADS")
        .forEach { environment.remove(it) }
    testLogging {
        events("failed")
        exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
    }
    // Calcite reflects heavily over its metadata handlers; give the forked JVM headroom.
    maxHeapSize = "2g"
    // DigestFixturesTest reads (and, with CHALK_WRITE_FIXTURES=1, writes) src/test/resources.
    systemProperty("chalk.projectDir", projectDir.absolutePath)
}

tasks.shadowJar {
    archiveClassifier = "all"
    mergeServiceFiles()
    manifest {
        attributes["Main-Class"] = "chalk.planner.Main"
        attributes["Implementation-Title"] = "chalk-planner"
        attributes["Implementation-Version"] = project.version.toString()
    }
    // Calcite ships a Janino-compiled runtime we never use (scan() throws); keep it anyway
    // because RexExecutorImpl constant folding goes through it.
}

tasks.named("build") {
    dependsOn(tasks.shadowJar)
}

// Writes the resolved versions where PlannerConfig can read them at runtime.
val generateBuildInfo by tasks.registering {
    val outputDir = layout.buildDirectory.dir("generated/buildinfo")
    val plannerVersion = project.version.toString()
    outputs.dir(outputDir)
    inputs.property("plannerVersion", plannerVersion)
    inputs.property("calciteVersion", calciteVersion)
    doLast {
        val dir = outputDir.get().asFile.resolve("chalk/planner")
        dir.mkdirs()
        dir.resolve("BuildInfo.properties").writeText(
            "planner.version=$plannerVersion\ncalcite.version=$calciteVersion\n",
        )
    }
}

sourceSets.main {
    resources.srcDir(generateBuildInfo)
}
