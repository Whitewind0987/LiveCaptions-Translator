using LiveCaptionsTranslator.models;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class AudioSettingTests
{
    [Fact]
    public void OldJsonWithoutAudioDeviceLoadsNullAndKeepsOtherSettings()
    {
        var path = TemporaryJson("{\"TargetLanguage\":\"fr-FR\"}");
        try
        {
            var setting = Setting.Load(path);
            Assert.Null(setting.AudioOutputDeviceId);
            Assert.Equal("fr-FR", setting.TargetLanguage);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrWhitespaceMeansSystemDefault(string? value)
    {
        Assert.Null(Setting.NormalizeAudioOutputDeviceId(value));
    }

    [Fact]
    public void DeviceIdIsTrimmed()
    {
        Assert.Equal("endpoint-id", Setting.NormalizeAudioOutputDeviceId(" endpoint-id "));
    }

    [Fact]
    public void ValidEndpointIdRoundTripsWithoutChangingUnrelatedSetting()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lct-setting-{Guid.NewGuid():N}.json");
        try
        {
            var setting = Setting.Load(path);
            setting.TargetLanguage = "de-DE";
            setting.AudioOutputDeviceId = "endpoint-id";

            var loaded = Setting.Load(path);
            Assert.Equal("endpoint-id", loaded.AudioOutputDeviceId);
            Assert.Equal("de-DE", loaded.TargetLanguage);
        }
        finally { File.Delete(path); }
    }

    private static string TemporaryJson(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lct-setting-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
