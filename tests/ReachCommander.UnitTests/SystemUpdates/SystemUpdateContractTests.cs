using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReachCommander.Application.SystemUpdates;

namespace ReachCommander.UnitTests.SystemUpdates;

public sealed class SystemUpdateContractTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-25T10:00:00Z");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void Status_serializes_logical_versions_without_host_or_full_digest()
    {
        var status = SystemUpdateStatusFactory.Available(
            "stable",
            "v1.3.0",
            "v1.4.0",
            Now,
            Now);

        var json = JsonSerializer.Serialize(status, JsonOptions);

        Assert.Contains("\"phase\":\"available\"", json);
        Assert.Contains("\"targetVersion\":\"v1.4.0\"", json);
        Assert.DoesNotContain("/opt/reachcommander", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256:", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Applying_status_serializes_only_the_logical_progress_stage()
    {
        var status = SystemUpdateStatusFactory.Applying(
            "stable",
            "v1.3.0",
            "v1.4.0",
            "operation-1",
            Now,
            Now,
            SystemUpdateProgressStage.Downloading);

        var json = JsonSerializer.Serialize(status, JsonOptions);

        Assert.Contains("\"progressStage\":\"downloading\"", json);
        Assert.DoesNotContain("docker", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256:", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_contract_accepts_no_target_input()
    {
        var method = typeof(ISystemUpdateService).GetMethod(nameof(ISystemUpdateService.ApplyAsync));

        Assert.Equal(
            [typeof(CancellationToken)],
            method!.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void Factory_uses_supported_protocol_and_enforces_apply_invariants()
    {
        var statuses = new[]
        {
            SystemUpdateStatusFactory.Unavailable(Now),
            SystemUpdateStatusFactory.Incompatible(Now),
            SystemUpdateStatusFactory.Checking(Now),
            SystemUpdateStatusFactory.Current("stable", "v1.3.0", Now, Now),
            SystemUpdateStatusFactory.Pinned("v1.3.0", "v1.3.0", Now, Now),
            SystemUpdateStatusFactory.Blocked("stable", "v1.3.0", "v1.4.0", Now, Now),
            SystemUpdateStatusFactory.Applying("stable", "v1.3.0", "v1.4.0", "operation-1", Now, Now),
            SystemUpdateStatusFactory.Completed("stable", "v1.3.0", "v1.4.0", "operation-1", Now, Now),
            SystemUpdateStatusFactory.RolledBack("stable", "v1.3.0", "v1.4.0", "operation-1", Now, Now),
            SystemUpdateStatusFactory.Failed("stable", "v1.3.0", "v1.4.0", "operation-1", Now, Now),
        };

        Assert.All(statuses, status => Assert.Equal(SystemUpdateStatusFactory.ProtocolVersion, status.ProtocolVersion));
        Assert.All(statuses, status => Assert.False(status.CanApply));

        var available = SystemUpdateStatusFactory.Available("stable", "v1.3.0", "v1.4.0", Now, Now);
        Assert.True(available.UpdateAvailable);
        Assert.True(available.CanApply);
        Assert.Equal(SystemUpdatePhase.Available, available.Phase);
    }

    [Theory]
    [InlineData(SystemUpdatePhase.Unavailable, "system_update_unavailable")]
    [InlineData(SystemUpdatePhase.Checking, "system_update_checking")]
    [InlineData(SystemUpdatePhase.Current, "up_to_date")]
    [InlineData(SystemUpdatePhase.Available, "update_available")]
    [InlineData(SystemUpdatePhase.Blocked, "system_update_blocked_by_operations")]
    [InlineData(SystemUpdatePhase.Applying, "update_applying")]
    [InlineData(SystemUpdatePhase.Completed, "update_completed")]
    [InlineData(SystemUpdatePhase.RolledBack, "candidate_rolled_back")]
    [InlineData(SystemUpdatePhase.Failed, "update_failed")]
    public void Factory_maps_each_phase_to_a_stable_reason(
        SystemUpdatePhase expectedPhase,
        string expectedReason)
    {
        var status = CreateStatus(expectedPhase);

        Assert.Equal(expectedPhase, status.Phase);
        Assert.Equal(expectedReason, status.ReasonCode);
        Assert.False(string.IsNullOrWhiteSpace(status.Detail));
    }

    [Fact]
    public void Factory_bounds_and_flattens_public_detail()
    {
        var status = SystemUpdateStatusFactory.Unavailable(
            Now,
            detail: new string('x', 300) + "\r\n/opt/reachcommander");

        Assert.NotNull(status.Detail);
        Assert.Equal(SystemUpdateStatusFactory.MaximumDetailLength, status.Detail!.Length);
        Assert.DoesNotContain('\r', status.Detail);
        Assert.DoesNotContain('\n', status.Detail);
        Assert.DoesNotContain("/opt/reachcommander", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, "v1.4.0")]
    [InlineData("", "v1.4.0")]
    [InlineData("v1.3.0", null)]
    [InlineData("v1.3.0", "")]
    public void Available_requires_current_and_target_versions(string? current, string? target)
    {
        Assert.Throws<ArgumentException>(() =>
            SystemUpdateStatusFactory.Available("stable", current!, target!, Now, Now));
    }

    [Theory]
    [InlineData(typeof(SystemUpdateUnavailableException), "system_update_unavailable")]
    [InlineData(typeof(SystemUpdateProtocolIncompatibleException), "system_update_protocol_incompatible")]
    [InlineData(typeof(SystemUpdateCheckRateLimitedException), "system_update_check_rate_limited")]
    [InlineData(typeof(SystemUpdateBlockedByOperationsException), "system_update_blocked_by_operations")]
    [InlineData(typeof(SystemUpdateInProgressException), "system_update_in_progress")]
    [InlineData(typeof(SystemUpdateFailedException), "system_update_failed")]
    public void Stable_exceptions_expose_expected_code(Type exceptionType, string expectedCode)
    {
        var exception = (SystemUpdateException)Activator.CreateInstance(exceptionType)!;

        Assert.Equal(expectedCode, exception.Code);
        Assert.False(string.IsNullOrWhiteSpace(exception.PublicDetail));
    }

    private static SystemUpdateStatus CreateStatus(SystemUpdatePhase phase) => phase switch
    {
        SystemUpdatePhase.Unavailable => SystemUpdateStatusFactory.Unavailable(Now),
        SystemUpdatePhase.Checking => SystemUpdateStatusFactory.Checking(Now),
        SystemUpdatePhase.Current => SystemUpdateStatusFactory.Current("stable", "v1.3.0", Now, Now),
        SystemUpdatePhase.Available => SystemUpdateStatusFactory.Available("stable", "v1.3.0", "v1.4.0", Now, Now),
        SystemUpdatePhase.Blocked => SystemUpdateStatusFactory.Blocked("stable", "v1.3.0", "v1.4.0", Now, Now),
        SystemUpdatePhase.Applying => SystemUpdateStatusFactory.Applying("stable", "v1.3.0", "v1.4.0", "operation-1", Now, Now),
        SystemUpdatePhase.Completed => SystemUpdateStatusFactory.Completed("stable", "v1.3.0", "v1.4.0", "operation-1", Now, Now),
        SystemUpdatePhase.RolledBack => SystemUpdateStatusFactory.RolledBack("stable", "v1.3.0", "v1.4.0", "operation-1", Now, Now),
        SystemUpdatePhase.Failed => SystemUpdateStatusFactory.Failed("stable", "v1.3.0", "v1.4.0", "operation-1", Now, Now),
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };
}
