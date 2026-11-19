using WuHealthGuard.Core.Abstractions;

namespace WuHealthGuard.Core.Tests;

public class ComponentStoreProtocolTests
{
    [Theory]
    [InlineData(ComponentStoreState.Healthy, null)]
    [InlineData(ComponentStoreState.Repairable, null)]
    [InlineData(ComponentStoreState.NotRepairable, null)]
    [InlineData(ComponentStoreState.Unknown, "DISM failed (0x800F082F)")]
    public void Results_round_trip(ComponentStoreState state, string? message)
    {
        var parsed = ComponentStoreProtocol.TryParseResult(ComponentStoreProtocol.Result(new ComponentStoreHealth(state, message)));
        Assert.Equal(new ComponentStoreHealth(state, message), parsed);
    }

    [Fact]
    public void Multi_line_messages_become_one_line() =>
        Assert.Equal("result Unknown a b", ComponentStoreProtocol.Result(new ComponentStoreHealth(ComponentStoreState.Unknown, "a\r\nb")));

    [Theory]
    [InlineData("progress 0", 0)]
    [InlineData("progress 42", 42)]
    [InlineData("progress 100", 100)]
    public void Progress_parses(string line, int expected)
    {
        Assert.True(ComponentStoreProtocol.TryParseProgress(line, out var percent));
        Assert.Equal(expected, percent);
    }

    [Theory]
    [InlineData("progress -1")]
    [InlineData("progress 101")]
    [InlineData("progress x")]
    [InlineData("Progress 5")]
    [InlineData("")]
    public void Bad_progress_is_ignored(string line) =>
        Assert.False(ComponentStoreProtocol.TryParseProgress(line, out _));

    [Theory]
    [InlineData("result")]
    [InlineData("result Broken")]
    [InlineData("result 7")]
    [InlineData("something else")]
    public void Bad_results_are_ignored(string line) =>
        Assert.Null(ComponentStoreProtocol.TryParseResult(line));
}
