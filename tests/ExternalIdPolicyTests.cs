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
        Assert.Equal("1981", System.Text.RegularExpressions.Regex.Match("1981 births", p.BirthYearTagPattern!).Groups["year"].Value);
        Assert.Matches(p.NotAnEntityTitlePattern!, "2022 in film");
        Assert.DoesNotMatch(p.NotAnEntityTitlePattern!, "Heat (film)");
    }
}
