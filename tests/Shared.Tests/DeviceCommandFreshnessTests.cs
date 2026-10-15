using IndustrialIot.Shared.Common;
using IndustrialIot.Shared.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace IndustrialIot.Shared.Tests;

public class DeviceCommandFreshnessTests
{
    private static readonly TimeSpan OneMs = TimeSpan.FromMilliseconds(1);

    [Fact]
    public void Boundaries()
    {
        var command = ContractRoundTripTests.SampleCommand();

        // FakeTimeProvider can only move forward, so the checks go from earliest to latest instant.
        // Edge clock behind the Cloud: more than 2 minutes is clock skew, exactly 2 minutes is tolerated.
        var clock = new FakeTimeProvider(command.IssuedAtUtc - DeviceCommand.MaxClockSkew - OneMs);
        Assert.Equal(CommandFreshness.ClockSkew, command.CheckFreshness(clock));

        clock.SetUtcNow(command.IssuedAtUtc - DeviceCommand.MaxClockSkew);
        Assert.Equal(CommandFreshness.Fresh, command.CheckFreshness(clock));

        // Expiry is inclusive: one millisecond before is fresh, exactly at expiresAtUtc is expired.
        clock.SetUtcNow(command.ExpiresAtUtc - OneMs);
        Assert.Equal(CommandFreshness.Fresh, command.CheckFreshness(clock));

        clock.SetUtcNow(command.ExpiresAtUtc);
        Assert.Equal(CommandFreshness.Expired, command.CheckFreshness(clock));
    }

    [Fact]
    public void ClockSkew_WhenEdgeClockIsHoursBehind()
    {
        // An Edge clock 3 hours slow must report a clock problem, not silently treat the command as fresh.
        var command = ContractRoundTripTests.SampleCommand();
        var clock = new FakeTimeProvider(command.IssuedAtUtc - TimeSpan.FromHours(3));

        Assert.Equal(CommandFreshness.ClockSkew, command.CheckFreshness(clock));
    }
}
