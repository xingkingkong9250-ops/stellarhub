using QL.World.Services.Ai;

namespace QL.World.Tests.Ai;

public sealed class DisabledAiServiceTests
{
    [Fact]
    public void Mode_is_disabled()
    {
        var ai = new DisabledAiService();
        Assert.Equal("Disabled", ai.Mode);
    }

    [Fact]
    public void IsAvailable_is_false()
    {
        var ai = new DisabledAiService();
        Assert.False(ai.IsAvailable);
    }

    [Fact]
    public void SupportsEmbeddings_is_false()
    {
        var ai = new DisabledAiService();
        Assert.False(ai.SupportsEmbeddings);
    }

    [Fact]
    public async Task AnalyzeImage_returns_error_result()
    {
        var ai = new DisabledAiService();
        var result = await ai.AnalyzeImageAsync("dummy.png", CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("未启用", result.ErrorMessage);
        Assert.Empty(result.Tags);
        Assert.Null(result.Embedding);
    }

    [Fact]
    public async Task TestConnection_returns_false()
    {
        var ai = new DisabledAiService();
        Assert.False(await ai.TestConnectionAsync(CancellationToken.None));
    }
}
