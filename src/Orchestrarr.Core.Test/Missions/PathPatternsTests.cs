using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

public sealed class PathPatternsTests
{
    [Theory]
    [InlineData(".github/**", ".github/workflows/ci.yml", true)]
    [InlineData(".github/**", ".github/CODEOWNERS", true)]
    [InlineData(".github/**", "src/.github/x", false)]
    [InlineData("**/*.enc.*", "secrets/prod.enc.yaml", true)]
    [InlineData("**/*.enc.*", "prod.enc.yaml", true)]
    [InlineData("**/*.enc.*", "src/prod.yaml", false)]
    [InlineData("**/.env*", "services/api/.env.production", true)]
    [InlineData("**/.env*", ".env", true)]
    [InlineData("src/*.cs", "src/Orders.cs", true)]
    [InlineData("src/*.cs", "src/sub/Orders.cs", false)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "a/c", false)]
    [InlineData(".GITHUB/**", ".github/x", true)]
    public void A_pattern_matches_the_paths_it_names_and_only_those(string pattern, string path, bool expected)
    {
        PathPatterns.Matches(pattern, path).Should().Be(expected);
    }

    [Fact]
    public void A_path_written_with_back_slashes_or_a_leading_slash_is_the_same_path()
    {
        PathPatterns.Matches(".github/**", ".github\\workflows\\ci.yml").Should().BeTrue();
        PathPatterns.Matches(".github/**", "/.github/workflows/ci.yml").Should().BeTrue();
    }

    [Fact]
    public void Any_changed_path_that_matches_any_pattern_is_enough()
    {
        PathPatterns.AnyMatches([".github/**", "**/*.enc.*"], ["src/a.cs", "x/y.enc.yaml"]).Should().BeTrue();
        PathPatterns.AnyMatches([".github/**"], ["src/a.cs"]).Should().BeFalse();
        PathPatterns.AnyMatches([], ["src/a.cs"]).Should().BeFalse();
    }
}
