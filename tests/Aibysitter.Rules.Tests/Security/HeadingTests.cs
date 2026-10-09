namespace Aibysitter.Rules.Tests;

public class HeadingTests
{
    [Theory]
    [InlineData("# a #", "a")]
    [InlineData("## C##", "C")]
    [InlineData("### x # #", "x #")]
    [InlineData("## End ##  ", "End")]
    [InlineData("#  ", "")]
    [InlineData("   # three spaces", "three spaces")]
    public void Heading_TextTrimmed(string line, string text)
    {
        var file = RulesFile.Parse(line + "\nbody");

        Assert.True(file.Lines[0].IsHeading);
        Assert.Equal(text, Assert.Single(file.Sections).Heading);
    }

    [Theory]
    [InlineData("#")]
    [InlineData("######")]
    [InlineData("#foo")]
    [InlineData("####### seven")]
    [InlineData("    # four spaces")]
    public void NotAHeading(string line) => Assert.False(RulesFile.Parse(line).Lines[0].IsHeading);

    [Theory]
    [InlineData("heading-3200")]
    [InlineData("heading-6400")]
    public void ReportInput_LintsFast(string name)
    {
        var text = SecurityInputs.Report.Single(i => i.Name == name).Text;
        var engine = new LintEngine();

        Timing.AssertFast(name, () => engine.Analyze(text));
        Assert.True(RulesFile.Parse(text).Lines[0].IsHeading);
    }
}
