using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.audio.windows;
using Xunit;

#pragma warning disable xUnit1051 // Endpoint fake operations complete synchronously.

namespace LiveCaptionsTranslator.Tests;

public sealed class AudioEndpointTests
{
    private static readonly AudioEndpointInfo Default =
        new("default", "Z Speakers", true, AudioEndpointAvailability.Active);
    private static readonly AudioEndpointInfo Saved =
        new("saved", "A Headphones", false, AudioEndpointAvailability.Active);

    [Fact]
    public void NullSavedIdResolvesDefaultEndpoint()
    {
        var result = AudioEndpointResolver.Resolve([Saved, Default], null);
        Assert.True(result.Success);
        Assert.Equal(Default, result.Endpoint);
        Assert.False(result.UsedFallback);
    }

    [Fact]
    public void ActiveSavedEndpointResolvesDirectly()
    {
        var result = AudioEndpointResolver.Resolve([Default, Saved], "saved");
        Assert.Equal(Saved, result.Endpoint);
        Assert.False(result.UsedFallback);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    public void MissingOrInactiveSavedEndpointFallsBackWithDiagnostic(string savedId)
    {
        var disabled = new AudioEndpointInfo(
            "disabled", "Disabled", false, AudioEndpointAvailability.Disabled);
        var result = AudioEndpointResolver.Resolve([Default, Saved, disabled], savedId);
        Assert.Equal(Default, result.Endpoint);
        Assert.True(result.UsedFallback);
        Assert.Contains(savedId, result.Diagnostic);
    }

    [Fact]
    public void NoActiveDefaultIsUnavailable()
    {
        var result = AudioEndpointResolver.Resolve([Saved], null);
        Assert.False(result.Success);
        Assert.Contains("default", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrderingIsDefaultFirstThenNameAndRemovesDuplicateIds()
    {
        var duplicate = Saved with { DisplayName = "Duplicate" };
        var ordered = WindowsAudioEndpointProvider.OrderAndDeduplicate(
            [Saved, duplicate, Default, new("third", "B Device", false, AudioEndpointAvailability.Active)]);
        Assert.Equal(new[] { "default", "saved", "third" }, ordered.Select(endpoint => endpoint.Id));
    }

    [Fact]
    public async Task EnumerationFailureIsTypedAndProviderDisposes()
    {
        var provider = new FakeAudioEndpointProvider
        {
            EnumerationResult = AudioEndpointEnumerationResult.Unavailable("enumeration failed")
        };
        var result = await provider.EnumerateAsync();
        await provider.DisposeAsync();
        Assert.False(result.Success);
        Assert.Equal("enumeration failed", result.FailureReason);
        Assert.Equal(1, provider.DisposeCount);
    }
}
