using System;
using System.Collections.Generic;
using System.IO;
using RoeSnip;
using RoeSnip.App;
using RoeSnip.Sharing;
using Xunit;

namespace RoeSnip.Tests.Sharing;

/// <summary>ShareProviders/DefaultShareProviderId are additive RoeSnipSettings fields (per the track
/// brief) - this exercises SettingsStore's existing JSON round-trip contract against them
/// specifically, including a built-in config's Values and a full inline Custom ProviderSpec, the
/// same isolated-temp-path convention SettingsTests.cs already uses.</summary>
public class ShareProviderSettingsPersistenceTests : IDisposable
{
    private readonly string _tempDir;

    public ShareProviderSettingsPersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"roesnip_share_settings_test_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string PathFor(string fileName) => Path.Combine(_tempDir, fileName);

    [Fact]
    public void Default_ShareProviders_IsEmpty()
    {
        Assert.Empty(RoeSnipSettings.Default.ShareProviders);
        Assert.Null(RoeSnipSettings.Default.DefaultShareProviderId);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsBuiltInProviderConfig()
    {
        Directory.CreateDirectory(_tempDir);
        string settingsPath = PathFor("settings.json");

        var original = new RoeSnipSettings
        {
            DefaultShareProviderId = "roeshare",
            ShareProviders = new List<ShareProviderConfig>
            {
                new()
                {
                    Id = "roeshare",
                    SpecId = "roeshare",
                    DisplayName = "My RoeShare",
                    Enabled = true,
                    Values = new Dictionary<string, string> { ["BaseUrl"] = "https://share.example.com", ["ApiKey"] = "rsk_abc" },
                },
            },
        };

        SettingsStore.Save(original, settingsPath);
        var loaded = SettingsStore.Load(settingsPath);

        Assert.Equal("roeshare", loaded.DefaultShareProviderId);
        var roeshare = Assert.Single(loaded.ShareProviders);
        Assert.Equal("roeshare", roeshare.SpecId);
        Assert.True(roeshare.Enabled);
        Assert.Equal("https://share.example.com", roeshare.Values["BaseUrl"]);
        Assert.Equal("rsk_abc", roeshare.Values["ApiKey"]);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsCustomProviderSpec()
    {
        Directory.CreateDirectory(_tempDir);
        string settingsPath = PathFor("settings.json");

        var customSpec = new ProviderSpec
        {
            Id = "custom-abc",
            Name = "My Custom Uploader",
            Endpoint = "https://uploads.example.com/api/{ApiKey}",
            Method = "POST",
            UploadKind = ShareUploadKind.Multipart,
            MultipartFieldName = "attachment",
            ExtraFields = new Dictionary<string, string> { ["type"] = "image" },
            Headers = new Dictionary<string, string> { ["X-Token"] = "{ApiKey}" },
            MaxUploadBytes = 10 * 1024 * 1024,
            ResponseMode = ResponseUrlMode.Regex,
            ResponseRegex = @"href=""([^""]+)""",
            IsBuiltIn = false,
            Verified = false,
        };

        var original = new RoeSnipSettings
        {
            ShareProviders = new List<ShareProviderConfig>
            {
                new()
                {
                    Id = "custom-1",
                    IsCustom = true,
                    CustomSpec = customSpec,
                    DisplayName = "My uploader",
                    Enabled = true,
                    Values = new Dictionary<string, string> { ["ApiKey"] = "secret" },
                },
            },
        };

        SettingsStore.Save(original, settingsPath);
        var loaded = SettingsStore.Load(settingsPath);

        var config = Assert.Single(loaded.ShareProviders);
        Assert.True(config.IsCustom);
        Assert.NotNull(config.CustomSpec);
        Assert.Equal("My Custom Uploader", config.CustomSpec!.Name);
        Assert.Equal(ShareUploadKind.Multipart, config.CustomSpec.UploadKind);
        Assert.Equal("attachment", config.CustomSpec.MultipartFieldName);
        Assert.Equal("image", config.CustomSpec.ExtraFields["type"]);
        Assert.Equal("{ApiKey}", config.CustomSpec.Headers["X-Token"]);
        Assert.Equal(10 * 1024 * 1024, config.CustomSpec.MaxUploadBytes);
        Assert.Equal(ResponseUrlMode.Regex, config.CustomSpec.ResponseMode);
        Assert.Equal("secret", config.Values["ApiKey"]);
    }

    [Fact]
    public void Load_JsonWithoutShareProvidersField_FallsBackToEmptyList()
    {
        Directory.CreateDirectory(_tempDir);
        string settingsPath = PathFor("settings.json");
        File.WriteAllText(settingsPath, """
            {
              "SchemaVersion": 1,
              "CopyOnSelect": true
            }
            """);

        var loaded = SettingsStore.Load(settingsPath);

        Assert.Empty(loaded.ShareProviders);
        Assert.Null(loaded.DefaultShareProviderId);
        Assert.True(loaded.CopyOnSelect);
    }
}
