using Chronicle.Plugin.Wikipedia;
using Xunit;

namespace Chronicle.Plugin.Wikipedia.Tests;

public class ExternalIdPolicyTests
{
    [Fact]
    public void TheProviderDeclaresHowItsIdsBehave()
    {
        var p = new WikipediaMetadataProvider().GetExternalIdPolicy();
        Assert.Equal("wikipedia", p.Source);
        Assert.True(p.NameDerived);
        Assert.Equal("title", p.DisambiguatedTitleField);
    }
}
