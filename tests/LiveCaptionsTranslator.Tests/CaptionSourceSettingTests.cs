using System.Text.Json;

using LiveCaptionsTranslator.captioning;
using LiveCaptionsTranslator.models;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class CaptionSourceSettingTests
{
    [Fact]
    public void NewSettingDefaultsToWindowsLiveCaptions()
    {
        Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, new Setting().CaptionSource);
    }

    [Fact]
    public void OldJsonWithoutCaptionSourceDefaultsToWindowsAndPreservesExistingValues()
    {
        var path = TemporaryJson("{\"TargetLanguage\":\"fr-FR\"}");
        try
        {
            var setting = Setting.Load(path);

            Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, setting.CaptionSource);
            Assert.Equal("fr-FR", setting.TargetLanguage);
            Assert.NotEmpty(setting.Configs);
            Assert.NotEmpty(setting.ConfigIndices);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(CaptionSourceKind.WindowsLiveCaptions, "WindowsLiveCaptions")]
    [InlineData(CaptionSourceKind.LocalAsr, "LocalAsr")]
    public void SupportedSourceRoundTripsAsStableString(
        CaptionSourceKind source,
        string persistedValue)
    {
        var path = TemporaryPath();
        try
        {
            var setting = Setting.Load(path);
            setting.CaptionSource = source;
            setting.Save();

            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            var sourceElement = document.RootElement.GetProperty("CaptionSource");
            Assert.Equal(JsonValueKind.String, sourceElement.ValueKind);
            Assert.Equal(persistedValue, sourceElement.GetString());
            Assert.DoesNotContain($"\"CaptionSource\": {(int)source}", json, StringComparison.Ordinal);

            File.WriteAllText(
                path,
                JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["CaptionSource"] = persistedValue
                }));
            Assert.Equal(source, Setting.Load(path).CaptionSource);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"Unknown\"")]
    [InlineData("\"localasr\"")]
    [InlineData("\"Local ASR\"")]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void UnsupportedOrMalformedSourceValueNormalizesWithoutThrowing(string jsonValue)
    {
        var path = TemporaryJson($"{{\"CaptionSource\":{jsonValue}}}");
        try
        {
            var setting = Setting.Load(path);

            Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, setting.CaptionSource);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadNormalizationDoesNotPrematurelyAutosave()
    {
        const string json = "{\"CaptionSource\":\"unsupported\",\"TargetLanguage\":\"de-DE\"}";
        var path = TemporaryJson(json);
        try
        {
            var setting = Setting.Load(path);

            Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, setting.CaptionSource);
            Assert.Equal("de-DE", setting.TargetLanguage);
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RuntimeUnsupportedEnumNormalizesToWindows()
    {
        var setting = new Setting { CaptionSource = (CaptionSourceKind)999 };

        Assert.Equal(CaptionSourceKind.WindowsLiveCaptions, setting.CaptionSource);
    }

    private static string TemporaryJson(string json)
    {
        var path = TemporaryPath();
        File.WriteAllText(path, json);
        return path;
    }

    private static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), $"lct-stage64-setting-{Guid.NewGuid():N}.json");
}
