using Orchestrarr.Core.Infrastructure;

namespace Orchestrarr.Core.Test.Infrastructure;

public sealed class PathGlobTests
{
    [Theory]
    [InlineData("apps/**/*.yaml", "apps/web/deployment.yaml", true)]
    [InlineData("apps/**/*.yaml", "apps/deployment.yaml", true)]
    [InlineData("apps/**/*.yaml", "apps/a/b/c.yaml", true)]
    [InlineData("apps/**/*.yaml", "other/web/deployment.yaml", false)]
    [InlineData("apps/**/*.yaml", "apps/web/deployment.yml", false)]
    [InlineData("terraform/*.tf", "terraform/main.tf", true)]
    [InlineData("terraform/*.tf", "terraform/modules/main.tf", false)]
    [InlineData("docker-compose.yml", "docker-compose.yml", true)]
    [InlineData("docker-compose.yml", "dockerXcompose.yml", false)]
    [InlineData("k8s/**", "k8s/a/b.yaml", true)]
    [InlineData("a?.yaml", "ab.yaml", true)]
    [InlineData("a?.yaml", "a/.yaml", false)]
    public void A_pattern_matches_the_paths_it_describes(string pattern, string path, bool expected) => PathGlob.IsMatch(pattern, path).Should().Be(expected);

    [Theory]
    [InlineData("apps/portarr/secret-env.enc.yaml", true)]
    [InlineData("terraform/terraform.tfstate", true)]
    [InlineData("terraform/terraform.tfstate.backup", true)]
    [InlineData("terraform/terraform.tfvars", true)]
    [InlineData("app/.env", true)]
    [InlineData("deploy/Secret.yaml", true)]
    [InlineData("apps/portarr/deployment.yaml", false)]
    [InlineData("terraform/variables.tf", false)]
    [InlineData("terraform/terraform.tfvars.example", true)]
    public void A_file_that_holds_a_secret_is_never_read(string path, bool secret) => PathGlob.IsSecretFile(path).Should().Be(secret);
}
