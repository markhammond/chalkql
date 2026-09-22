using Chalk.Client;
using Chalk.Catalog;
using Chalk.Ir;
using Chalk.Sources.Ado;

namespace Chalk.Sample.Tutorial;

/// <summary>
/// One query, one database, with a small domain function compiled into the SQL.
/// </summary>
public static class Chapter01OneAndDone
{
    public static async Task RunAsync(Func<IQueryPlanner> planner)
    {
        Tutorial.Chapter(
            1,
            "One and done",
            """
            Start with one ordinary SQLite database and one ordinary SQL query. Add a small domain
            function of our own, then let ChalkQL plan the query and SQLite get on with it.
            """);

        const string databaseSchema =
            """
            CREATE TABLE orders (
              order_id     INTEGER       NOT NULL PRIMARY KEY,
              customer_id  INTEGER       NOT NULL,
              order_date   TIMESTAMP     NOT NULL,
              freight      DECIMAL(28,2) NOT NULL
            );

            CREATE INDEX ix_orders_customer_date
              ON orders(customer_id, order_date);
            """;

        using var database = Database.Sqlite("shop");

        await ExecuteAsync(
            database,
            databaseSchema);

        await ExecuteAsync(
            database,
            """
            INSERT INTO orders VALUES
              ( 1, 1, '2026-03-02 09:00:00', 12.00),
              ( 2, 5, '2026-03-02 09:45:00', 54.75),
              ( 3, 3, '2026-03-02 10:30:00', 88.50),
              ( 4, 8, '2026-03-02 11:15:00', 36.75),
              ( 5, 5, '2026-03-02 12:00:00', 63.00),
              (11, 3, '2026-03-02 16:30:00', 42.00),
              (19, 3, '2026-03-02 22:30:00', 70.50),
              (27, 3, '2026-03-03 04:30:00', 24.75),
              (35, 3, '2026-03-03 10:30:00', 95.25);
            """);

        Tutorial.Step("the database");
        Tutorial.Block("schema", databaseSchema);

        var source = new AdoSourceBuilder(
                database.SourceId,
                database.Open,
                "main")
            .Dialect(database.Dialect)
            .Capabilities(AdoCapabilities.For(database.Dialect))
            .RowCounts(AdoSourceBuilder.RowCountMode.Exact)
            .AddTable(
                "orders",
                configure: table => table
                    .UniqueKey("order_id")
                    .Index(
                        "ix_orders_customer_date",
                        unique: false,
                        "customer_id",
                        "order_date"))
            .AddFunction(
                "freight_with_discount",
                function => function
                    .Scalar()
                    .Parameter(
                        "freight",
                        ChalkType.Decimal(28, 2))
                    .Parameter(
                        "discount",
                        ChalkType.Decimal(5, 4))
                    .Returns(
                        ChalkType.Decimal(28, 2))
                    .Strict()
                    .Sql(
                        """
                        CAST(
                          freight * (
                            1 - CASE
                                  WHEN discount < 0 THEN 0
                                  WHEN discount > 1 THEN 1
                                  ELSE discount
                                END
                          )
                          AS DECIMAL(28, 2)
                        )
                        """))
            .Build();

        Tutorial.Step("what ChalkQL knows");
        Tutorial.Catalog("catalog", source.DescribeSchema());
        
        await using var engine = await ChalkEngine.CreateAsync(
            new ChalkEngineOptions
            {
                ContextId = "tutorial-01",
                Sources = [source],
                Planner = planner(),
            });

        const string sql =
            """
            SELECT
              order_id,
              order_date,
              freight,
              freight_with_discount(freight, 0.10) AS discounted_freight
            FROM orders
            WHERE customer_id = 3
            ORDER BY order_date
            """;

        Tutorial.Step("the query");
        Tutorial.Sql(sql);

        var query = await engine.PrepareAsync(sql, new PrepareOptions { IncludePlanText = true });

        var sourceQueries = query.Plan.SourceQueries().ToList();

        Tutorial.Step("what SQLite receives");
        Tutorial.PrintSourceQueries(query.Plan);

        Tutorial.Step("what SQLite will do");

        Tutorial.Block(
            "sqlite plan",
            database.Explain(sourceQueries.Single().QueryText));

        Tutorial.Step("what ChalkQL planned");
        Tutorial.Block(
            "chalkql plan",
            query.PlanText ?? "(none)");

        Tutorial.Step("the result");

        await using var execution = await engine.ExecuteAsync(query);
        await Tutorial.PrintAsync(execution);

        Tutorial.Block(
            "why this matters",
            """
            `freight_with_discount` is an application-defined function, but SQLite never needs to
            know that name. ChalkQL expands its SQL definition while planning, so the database
            receives ordinary SQL containing the calculation itself.

            The rest is deliberately unremarkable. SQLite applies `customer_id = 3`, uses its
            index to satisfy the filtering and ordering, evaluates the expanded expression, and
            returns the rows.

            One query, one database. Next, the SQL stays ordinary — but what a caller is allowed
            to know becomes part of the plan.
            """);

        static async Task ExecuteAsync(
            Database database,
            string sql)
        {
            await using var connection = database.Open();
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            await command.ExecuteNonQueryAsync();
        }
    }
}