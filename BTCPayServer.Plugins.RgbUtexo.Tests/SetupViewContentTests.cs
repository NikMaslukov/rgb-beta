using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class SetupViewContentTests
{
    [Fact]
    public void SetupCshtml_HasConsentCheckbox_InAllThreeForms()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Views", "RGB", "Setup.cshtml"));
        Assert.True(File.Exists(path), $"Could not locate Setup.cshtml at {path}");

        var content = File.ReadAllText(path);

        var formMatches = Regex.Matches(content,
            @"<form[^>]*>(.*?)</form>",
            RegexOptions.Singleline);
        Assert.Equal(3, formMatches.Count);

        foreach (Match form in formMatches)
        {
            var body = form.Groups[1].Value;
            Assert.Contains("name=\"AcknowledgesCustodialRisk\"", body);
            Assert.Contains("Custodial hot wallet", body);
        }

        Assert.Contains("id=\"AcknowledgesCustodialRisk_create\"", content);
        Assert.Contains("id=\"AcknowledgesCustodialRisk_restore\"", content);
        Assert.Contains("id=\"AcknowledgesCustodialRisk_backup\"", content);
    }
}
