using Chalk.Client;
using Chalk.Client.Rpc;
using Google.Protobuf;
using Grpc.Core;

namespace Chalk.Integration.Tests;

/// <summary>
/// The transport's own logic, with no server: how a gRPC failure becomes something a host can act on
/// (docs/design/04-client.md §9, D25). The trailer's *round trip* over a real channel is covered by
/// <see cref="PlannerErrorTests"/> against the live sidecar, which is a stronger test than a fake
/// service would be; what is left here is the decoding, which has edge cases a live test would not
/// reach.
/// </summary>
public sealed class GrpcQueryPlannerTests
{
    private static RpcException WithTrailer(StatusCode code, PlanError error)
    {
        var trailers = new Metadata();
        trailers.Add("chalk-plan-error-bin", error.ToByteArray());
        return new RpcException(new Status(code, error.Message), trailers);
    }

    [Fact]
    public void A_plan_error_trailer_is_decoded()
    {
        var error = WithTrailer(
            StatusCode.InvalidArgument,
            new PlanError
            {
                Kind = PlanErrorKind.Parse,
                Message = "Encountered \"close\"",
                Line = 1,
                Column = 20,
                EndLine = 1,
                EndColumn = 24,
            });

        var decoded = GrpcQueryPlanner.DecodeTrailer(error);

        Assert.NotNull(decoded);
        Assert.Equal(PlanErrorKind.Parse, decoded!.Kind);
        Assert.Equal(20, decoded.Column);
    }

    [Fact]
    public void A_failure_with_no_trailer_decodes_to_nothing_rather_than_throwing()
    {
        var error = new RpcException(new Status(StatusCode.Internal, "boom"));

        Assert.Null(GrpcQueryPlanner.DecodeTrailer(error));
    }

    /// <summary>
    /// A trailer we cannot parse is worse than none: failing while reporting a failure loses the
    /// original error entirely.
    /// </summary>
    [Fact]
    public void An_unparseable_trailer_decodes_to_nothing_rather_than_throwing()
    {
        var trailers = new Metadata();
        trailers.Add("chalk-plan-error-bin", [0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        var error = new RpcException(new Status(StatusCode.Internal, "boom"), trailers);

        Assert.Null(GrpcQueryPlanner.DecodeTrailer(error));
    }

    [Fact]
    public void A_non_binary_trailer_of_the_same_name_is_ignored()
    {
        var trailers = new Metadata();
        trailers.Add("chalk-plan-error", "not the binary one");
        var error = new RpcException(new Status(StatusCode.Internal, "boom"), trailers);

        Assert.Null(GrpcQueryPlanner.DecodeTrailer(error));
    }

    [Theory]
    [InlineData(PlanErrorKind.Parse, "SQL parse error")]
    [InlineData(PlanErrorKind.Validation, "SQL validation error")]
    [InlineData(PlanErrorKind.Unsupported, "Unsupported query")]
    [InlineData(PlanErrorKind.EpochMismatch, "Catalog epoch mismatch")]
    [InlineData(PlanErrorKind.Internal, "Planner internal error")]
    public void Each_error_kind_reads_as_something_a_host_can_act_on(PlanErrorKind kind, string expected)
    {
        var exception = new PlanningException(kind, "detail", position: null);

        Assert.StartsWith(expected, exception.Message, StringComparison.Ordinal);
        Assert.Equal(kind, exception.Kind);
    }

    [Fact]
    public void A_position_is_rendered_as_a_range_when_the_planner_gave_one()
    {
        Assert.Equal("line 1, column 20", new SqlPosition(1, 20, 1, 20).ToString());
        Assert.Equal("line 1, column 20 to line 1, column 24", new SqlPosition(1, 20, 1, 24).ToString());
        Assert.Contains(
            "line 3, column 7",
            new PlanningException(PlanErrorKind.Validation, "nope", new SqlPosition(3, 7, 3, 11)).Message,
            StringComparison.Ordinal);
    }
}
