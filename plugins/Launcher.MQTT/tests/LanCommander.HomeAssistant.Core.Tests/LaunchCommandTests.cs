using System.Text;
using LanCommander.HomeAssistant.Core;
using Xunit;

namespace LanCommander.HomeAssistant.Core.Tests;

public sealed class LaunchCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 4, 33, 0, TimeSpan.Zero);

    [Fact]
    public void Parser_accepts_exact_launch_contract()
    {
        var requestId = Guid.NewGuid();
        var gameId = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes(
            $$"""{"requestId":"{{requestId}}","action":"launch","gameId":"{{gameId}}","issuedAt":"2026-10-29T04:33:00Z","expiresAt":"2026-10-29T04:33:30Z"}""");

        Assert.True(LaunchCommandParser.TryParse(payload, out var command));
        Assert.Equal(requestId, command!.RequestId);
        Assert.Equal(gameId, command.GameId);
        Assert.Null(LaunchCommandValidator.Validate(command, Now));
    }

    [Fact]
    public void Parser_rejects_oversized_and_unknown_payloads()
    {
        Assert.False(LaunchCommandParser.TryParse(new byte[LaunchCommandParser.MaximumPayloadBytes + 1], out _));
        Assert.False(LaunchCommandParser.TryParse(
            Encoding.UTF8.GetBytes("""{"requestId":"x","unexpected":"value"}"""),
            out _));
    }

    [Theory]
    [InlineData("start", "unsupported action")]
    [InlineData("launch", null)]
    [InlineData("install", null)]
    [InlineData("select", null)]
    [InlineData("launch_selected", null)]
    [InlineData("install_selected", null)]
    public void Validator_accepts_only_supported_actions(string action, string? expectedError)
    {
        var command = ValidCommand() with { Action = action };

        Assert.Equal(expectedError, LaunchCommandValidator.Validate(command, Now));
    }

    [Theory]
    [InlineData("launch", false)]
    [InlineData("install", false)]
    [InlineData("select", false)]
    [InlineData("launch_selected", true)]
    [InlineData("install_selected", true)]
    public void Only_selected_actions_can_omit_an_explicit_game(string action, bool accepted)
    {
        Assert.Equal(accepted, LaunchCommandValidator.Validate(
            ValidCommand() with { Action = action, GameId = Guid.Empty }, Now) is null);
    }

    [Fact]
    public void Validator_rejects_expired_or_unbounded_commands()
    {
        var expired = ValidCommand() with
        {
            IssuedAt = Now - TimeSpan.FromSeconds(30),
            ExpiresAt = Now
        };
        var overlong = ValidCommand() with { ExpiresAt = Now + TimeSpan.FromSeconds(31) };

        Assert.Equal("command has expired", LaunchCommandValidator.Validate(expired, Now));
        Assert.Equal("command lifetime exceeds the maximum", LaunchCommandValidator.Validate(overlong, Now));
    }

    private static LaunchCommand ValidCommand() => new(
        Guid.NewGuid(),
        "launch",
        Guid.NewGuid(),
        Now,
        Now + TimeSpan.FromSeconds(30));
}
