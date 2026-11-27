namespace Orchestrarr.MissionRunner.Test;

/// <summary>The settings of the free agent go into a script and a command line: only plain names and plain https addresses are accepted.</summary>
public sealed class FreeAgentSettingsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private RepairSettings Settings(Action<RepairSettings>? change = null)
    {
        var dockerfile = Path.Combine(_folder, "Dockerfile");
        var token = Path.Combine(_folder, "token");
        File.WriteAllText(dockerfile, "FROM scratch");
        File.WriteAllText(token, "t");
        var settings = new RepairSettings { FreeDockerfileFile = dockerfile, TokenFile = token };
        change?.Invoke(settings);
        return settings;
    }

    [Fact]
    public void Plain_settings_are_accepted()
    {
        var settings = Settings(change => change.FreeRepositoryUrl = "https://git.example/{0}.git");

        settings.Validate();

        settings.IsFreeConfigured.Should().BeTrue();
    }

    [Theory]
    [InlineData("http://github.com/{0}.git")]
    [InlineData("https://user:pw@github.com/{0}.git")]
    [InlineData("https://github.com/{0}.git;rm")]
    [InlineData("https://github.com/{0}$(x).git")]
    [InlineData("https://github.com/`x`/{0}.git")]
    [InlineData("https://github.com/ {0}.git")]
    [InlineData("https://github.com/repo.git")]
    public void An_address_that_could_carry_more_than_a_repository_is_refused(string url)
    {
        var settings = Settings(change => change.FreeRepositoryUrl = url);

        settings.Invoking(configured => configured.Validate()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void An_image_a_model_a_range_or_a_missing_file_is_refused()
    {
        Settings(change => change.FreeImage = "Bad Image").Invoking(configured => configured.Validate()).Should().Throw<InvalidOperationException>();
        Settings(change => change.Model = "x;y").Invoking(configured => configured.Validate()).Should().Throw<InvalidOperationException>();
        Settings(change => change.FreeMaxTurns = 0).Invoking(configured => configured.Validate()).Should().Throw<InvalidOperationException>();
        Settings(change => change.FreeMaxTurns = 1001).Invoking(configured => configured.Validate()).Should().Throw<InvalidOperationException>();
        Settings(change => change.FreeTimeoutMinutes = 601).Invoking(configured => configured.Validate()).Should().Throw<InvalidOperationException>();
        var missing = Settings();
        File.Delete(missing.FreeDockerfileFile!);
        missing.Invoking(configured => configured.Validate()).Should().Throw<InvalidOperationException>();
        Settings(change => change.FreeCredentialRenewMinutes = 4).Invoking(configured => configured.Validate()).Should().Throw<InvalidOperationException>();
        Settings(change => change.FreeCredentialRenewMinutes = 51).Invoking(configured => configured.Validate()).Should().Throw<InvalidOperationException>("a credential of the forge lasts an hour: it is renewed before it expires");
        Settings(change => change.FreeDockerfileFile = null).IsFreeConfigured.Should().BeFalse();
    }
}
