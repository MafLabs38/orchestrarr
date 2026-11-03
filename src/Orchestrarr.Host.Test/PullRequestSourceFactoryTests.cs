using Microsoft.Extensions.Configuration;

namespace Orchestrarr.Host.Test;

public sealed class PullRequestSourceFactoryTests
{
    [Theory]
    [InlineData("ghp_secret", null, "github", true)]
    [InlineData("ghp_secret", "pulls.json", "github", true)]
    [InlineData(null, "pulls.json", "sample", true)]
    [InlineData(null, null, "none", false)]
    [InlineData("  ", "  ", "none", false)]
    public void The_forge_follows_the_configuration_and_a_token_wins_over_a_sample(string? token, string? sampleFile, string expectedKind, bool configured)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GitHub:Token"] = token, [PullRequestSourceFactory.SampleFileKey] = sampleFile })
            .Build();

        var source = PullRequestSourceFactory.Create(configuration, "/srv", TimeProvider.System);

        source.Kind.Should().Be(expectedKind);
        source.Configured.Should().Be(configured);
    }

    [Fact]
    public void A_list_of_bots_in_the_configuration_is_read_as_a_list()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["GitHub:Token"] = "ghp_secret", ["GitHub:BotLogins:0"] = "my-bot" })
            .Build();

        var options = configuration.GetSection("GitHub").Get<Orchestrarr.Providers.GitHub.GitHubOptions>()!;

        options.BotLogins.Should().Equal("my-bot");
    }
}
