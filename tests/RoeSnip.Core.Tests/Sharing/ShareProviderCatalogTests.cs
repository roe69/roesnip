using System.Collections.Generic;
using System.Linq;
using RoeSnip.Core.Sharing;
using Xunit;

namespace RoeSnip.Core.Tests.Sharing;

public class ShareProviderCatalogTests
{
    [Fact]
    public void BuiltIns_HaveUniqueNonEmptyIds()
    {
        var ids = ShareProviderCatalog.BuiltIns.Select(s => s.Id).ToList();
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void BuiltIns_EachDeclaresAResponseExtractionRuleMatchingItsMode()
    {
        foreach (var spec in ShareProviderCatalog.BuiltIns)
        {
            switch (spec.ResponseMode)
            {
                case ResponseUrlMode.JsonPath:
                    Assert.False(string.IsNullOrWhiteSpace(spec.ResponseJsonPath));
                    break;
                case ResponseUrlMode.Regex:
                    Assert.False(string.IsNullOrWhiteSpace(spec.ResponseRegex));
                    break;
                case ResponseUrlMode.PlainBody:
                    break; // no extra field needed
            }
        }
    }

    [Fact]
    public void BuiltIns_EveryMultipartSpecHasANonEmptyFieldNameOrDefaultsToFile()
    {
        foreach (var spec in ShareProviderCatalog.BuiltIns.Where(s => s.UploadKind == ShareUploadKind.Multipart))
        {
            // MultipartFieldName may be null (ProviderSpecShareProvider defaults it to "file"), but
            // if set it must not be blank.
            if (spec.MultipartFieldName is not null)
            {
                Assert.False(string.IsNullOrWhiteSpace(spec.MultipartFieldName));
            }
        }
    }

    [Fact]
    public void GoFile_IsMarkedUntested()
    {
        // Per the track brief: anything that couldn't be verified against current public docs must
        // be marked untested rather than shipped as fact - GoFile's fixed-server assumption and
        // response shape came from unofficial docs only.
        Assert.False(ShareProviderCatalog.GoFile.Verified);
    }

    [Fact]
    public void EveryOtherBuiltIn_IsMarkedVerified()
    {
        foreach (var spec in ShareProviderCatalog.BuiltIns.Where(s => s.Id != ShareProviderCatalog.GoFile.Id))
        {
            Assert.True(spec.Verified, $"{spec.Id} should be marked Verified (or this test needs updating if that changed deliberately)");
        }
    }

    [Fact]
    public void FindBuiltIn_KnownId_ReturnsSpec()
    {
        Assert.Same(ShareProviderCatalog.RoeShare, ShareProviderCatalog.FindBuiltIn("roeshare"));
    }

    [Fact]
    public void FindBuiltIn_UnknownId_ReturnsNull()
    {
        Assert.Null(ShareProviderCatalog.FindBuiltIn("does-not-exist"));
    }

    [Fact]
    public void ResolveSpec_BuiltInConfig_ResolvesLiveCatalogEntry()
    {
        var config = new ShareProviderConfig { Id = "roeshare", SpecId = "roeshare", IsCustom = false };
        Assert.Same(ShareProviderCatalog.RoeShare, ShareProviderCatalog.ResolveSpec(config));
    }

    [Fact]
    public void ResolveSpec_CustomConfig_ResolvesInlineSpec()
    {
        var customSpec = new ProviderSpec { Id = "my-custom", Endpoint = "https://example.com/upload" };
        var config = new ShareProviderConfig { Id = "custom-1", IsCustom = true, CustomSpec = customSpec };
        Assert.Same(customSpec, ShareProviderCatalog.ResolveSpec(config));
    }

    [Fact]
    public void ResolveSpec_BuiltInConfigWithUnknownSpecId_ReturnsNull()
    {
        var config = new ShareProviderConfig { Id = "orphan", SpecId = "does-not-exist", IsCustom = false };
        Assert.Null(ShareProviderCatalog.ResolveSpec(config));
    }

    [Fact]
    public void DefaultConfigFor_IsDisabledWithEmptyValues()
    {
        var config = ShareProviderCatalog.DefaultConfigFor(ShareProviderCatalog.Imgur);
        Assert.False(config.Enabled);
        Assert.Empty(config.Values);
        Assert.Equal("imgur", config.Id);
        Assert.Equal("imgur", config.SpecId);
        Assert.False(config.IsCustom);
    }

    [Fact]
    public void DefaultConfigFor_Litterbox_SeedsSensibleTimeDefault()
    {
        var config = ShareProviderCatalog.DefaultConfigFor(ShareProviderCatalog.Litterbox);
        Assert.Equal("1h", config.Values["Time"]);
    }

    [Fact]
    public void EffectiveConfigs_EmptyPersisted_SeedsEveryBuiltIn()
    {
        var effective = ShareProviderCatalog.EffectiveConfigs(new List<ShareProviderConfig>());
        Assert.Equal(ShareProviderCatalog.BuiltIns.Count, effective.Count);
        foreach (var spec in ShareProviderCatalog.BuiltIns)
        {
            Assert.Contains(effective, c => c.SpecId == spec.Id && !c.IsCustom);
        }
    }

    [Fact]
    public void EffectiveConfigs_PersistedBuiltIn_IsNotDuplicated()
    {
        var persisted = new List<ShareProviderConfig>
        {
            new() { Id = "roeshare", SpecId = "roeshare", Enabled = true, Values = new Dictionary<string, string> { ["BaseUrl"] = "https://x" } },
        };

        var effective = ShareProviderCatalog.EffectiveConfigs(persisted);

        Assert.Equal(ShareProviderCatalog.BuiltIns.Count, effective.Count); // still one row per built-in, not one extra
        var roeshareRow = Assert.Single(effective, c => c.SpecId == "roeshare");
        Assert.True(roeshareRow.Enabled); // the persisted (touched) version won, not a fresh placeholder
    }

    [Fact]
    public void EffectiveConfigs_CustomEntries_ArePreservedAlongsideBuiltIns()
    {
        var persisted = new List<ShareProviderConfig>
        {
            new() { Id = "custom-1", IsCustom = true, CustomSpec = new ProviderSpec { Id = "c1", Endpoint = "https://x" } },
        };

        var effective = ShareProviderCatalog.EffectiveConfigs(persisted);

        Assert.Contains(effective, c => c.Id == "custom-1" && c.IsCustom);
        Assert.Equal(ShareProviderCatalog.BuiltIns.Count + 1, effective.Count);
    }
}
