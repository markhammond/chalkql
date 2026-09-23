# ChalkQL.Sources

Standard data-source implementations for [ChalkQL](https://www.nuget.org/packages/ChalkQL), the federated SQL query engine for .NET powered by [Apache Calcite](https://calcite.apache.org/).

[![ci](https://github.com/markhammond/chalkql/actions/workflows/ci.yml/badge.svg)](https://github.com/markhammond/chalkql/actions/workflows/ci.yml)

Targets .NET 10.

```bash
dotnet add package ChalkQL.Sources
```

`ChalkQL.Sources` depends on `ChalkQL`, so a separate reference to the core package is not required.

Included source implementations:

| Source                      |                                                                     |
|-----------------------------|---------------------------------------------------------------------|
| `Chalk.Sources.Poco`        | application-owned POCO collections exposed as relational tables     |
| `Chalk.Sources.Ado`         | ADO.NET sources with capability-aware SQL pushdown                  |
| `Chalk.Sources.Akade`       | Akade IndexedSet collections exposed as relational tables           |
| `Chalk.Sources.DuckDb`      | DuckDB integration with native data-chunk reading                   |
| `Chalk.Sources.Conformance` | verifies a source's declared capabilities against the source itself |

## POCO sources

Application-owned collections can be exposed directly without copying their rows into another store.

Tables may declare keys, collations, statistics and indexes so the planner can make use of structure the application already knows about.

```csharp
var source = new PocoSourceBuilder("mem")
    .AddTable("rates", rows, t => t
        .OrderedBy(r => r.Ts)
        .ThenBy(r => r.Currency)
        .UniqueKey(r => r.Ts, r => r.Currency))
    .Build();
```

## [Akade.IndexedSet](../../src/Chalk.Sources.Akade) sources

Application-owned collections can be exposed directly without copying their rows into another store.

Tables may declare keys, collations, statistics and indexes so the planner can make use of structure the application already knows about.

```csharp
var set = rows.ToIndexedSet(x => x.Id)
    .WithIndex(x => x.ProductId)
    .WithRangeIndex(x => x.Amount)
    .WithRangeIndex(x => x.UnitPrice)
    .Build();

var source = AkadeSource.From("purchases", set)
            .TableName("purchases")
            .NamingPolicy(PocoNamingPolicy.SnakeCase)
            .Build();

        _sidecar = await SidecarFixture.StartAsync();
        if (!_sidecar.IsAvailable)
        {
            throw new InvalidOperationException(_sidecar.SkipReason);
        }

        _engine = await ChalkEngine.CreateAsync(new ChalkEngineOptions
        {
            ContextId = "akade-source",
            Sources = [source],
            Planner = _sidecar.CreatePlanner(),
            Execution = new ExecutionOptions { BatchSize = 4096, OutputMemory = OutputMemory.Pooled },
        });

        _point = await _engine.PrepareAsync("SELECT id, amount FROM purchases WHERE product_id = ?");

```

## Remote SQL sources

ADO.NET and DuckDB sources declare which relational operations they can evaluate. ChalkQL pushes supported work to the source and executes the remainder inside the .NET host.

Queries may span several sources at once, including joins between remote databases and application-owned tables.

## Custom sources

`Chalk.Sources.Abstractions` is distributed with the core `ChalkQL` package. Implement `ISourceRuntime` when the built-in sources are not appropriate.

## Things that will bite you

Not all ADO.NET providers support zero-allocation reads. Whether ChalkQL can avoid intermediate allocations depends on the provider and the data-access path it exposes.

## Documentation

See the [ChalkQL guide](https://github.com/markhammond/chalkql/blob/main/docs/guide.md) for source configuration and extension points, and the [tutorial](https://github.com/markhammond/chalkql/blob/main/docs/tutorial.md) for worked federation examples.

The full project source and documentation are in the [ChalkQL repository](https://github.com/markhammond/chalkql).

## Licence

ChalkQL.Sources is licensed under the Apache License 2.0.
