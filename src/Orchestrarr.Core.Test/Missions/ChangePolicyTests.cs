using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

/// <summary>The policy judges what the forge says a branch holds, never what a machine says of it.</summary>
public sealed class ChangePolicyTests
{
    private const string Base = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static BranchFile File(string path, string status = "modified", int changes = 2, string? patch = "@@ -1 +1 @@\n-a\n+b") => new(path, status, changes, patch);

    private static BranchChange Change(params BranchFile[] files) => new(Base, 0, 1, files, true);

    [Theory]
    [InlineData("package.json")]
    [InlineData("package-lock.json")]
    [InlineData("requirements.txt")]
    [InlineData("src/server.js")]
    [InlineData("test/server.test.js")]
    [InlineData("tests/new_test.py")]
    public void What_a_developer_would_change_to_solve_a_problem_may_be_integrated_the_dependencies_the_tests_and_the_build_included(string path)
    {
        ChangePolicy.Refusal(Change(File(path))).Should().BeNull();
    }

    [Theory]
    [InlineData("deploy/.env")]
    [InlineData("secrets/db.txt")]
    [InlineData("config/server.key")]
    [InlineData("CLAUDE.md")]
    [InlineData(".claude/settings.json")]
    [InlineData(".git/config")]
    [InlineData(".npmrc")]
    [InlineData("frontend/.yarnrc.yml")]
    [InlineData("pip.conf")]
    [InlineData(".gitattributes")]
    [InlineData(".pypirc")]
    [InlineData(".netrc")]
    [InlineData(".yarnrc")]
    [InlineData(".mcp.json")]
    [InlineData("AGENTS.md")]
    [InlineData(".cursorrules")]
    [InlineData(".cursor/rules.md")]
    [InlineData("CLAUDE.local.md")]
    [InlineData("keys/server.pem")]
    [InlineData("keys/store.p12")]
    [InlineData("keys/store.pfx")]
    [InlineData("home/id_rsa")]
    [InlineData("home/id_ed25519.pub")]
    [InlineData("config/app.enc.yaml")]
    [InlineData("config/credentials.json")]
    [InlineData(".gitmodules")]
    public void Never_the_files_that_hold_a_secret_the_metadata_of_the_repository_or_the_files_that_instruct_an_agent(string path)
    {
        ChangePolicy.Refusal(Change(File(path))).Should().Contain("protected");
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".gitlab-ci.yml")]
    [InlineData("Jenkinsfile")]
    public void A_pipeline_is_maintenance_like_any_other_file_and_may_be_changed(string path)
    {
        ChangePolicy.IsProtected(path).Should().BeFalse("changing a pipeline is maintenance (the maintainer, 2026-11-26, ADR 0055)");
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("a b.txt")]
    [InlineData("quote'.txt")]
    public void A_path_that_is_not_plain_is_refused_not_escaped(string path)
    {
        ChangePolicy.Refusal(Change(File(path))).Should().Contain("not one that a change may carry");
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("renamed")]
    [InlineData("copied")]
    public void A_deletion_or_a_rename_is_not_a_change_that_is_carried(string status)
    {
        ChangePolicy.Refusal(Change(File("src/a.js", status))).Should().Contain("added and modified");
    }

    [Fact]
    public void The_frame_of_the_mission_adds_to_the_policy()
    {
        ChangePolicy.Refusal(Change(File("infra/prod.tf")), ["infra/**"]).Should().Contain("forbidden by the frame");
    }

    [Fact]
    public void The_count_and_the_completeness_of_the_list_are_bounded()
    {
        ChangePolicy.Refusal(Change()).Should().Contain("between 1 and");
        ChangePolicy.Refusal(Change([.. Enumerable.Range(0, ChangePolicy.MaxFiles + 1).Select(index => File($"f{index}.txt"))])).Should().Contain("between 1 and");
        ChangePolicy.Refusal(Change(File("a.txt")) with { FilesComplete = false }).Should().Contain("between 1 and");
    }

    [Fact]
    public void A_branch_that_does_not_contain_the_default_branch_is_not_integrated_the_change_is_made_again()
    {
        ChangePolicy.Refusal(Change(File("a.txt")) with { BehindBy = 3 }).Should().Contain("moved");
    }

    [Fact]
    public void A_file_that_the_forge_could_not_show_is_not_integrated_a_lock_file_included()
    {
        ChangePolicy.Refusal(Change(File("logo.png", "added", 0, null))).Should().Contain("too big, or is not text");
        ChangePolicy.Refusal(Change(File("empty.txt", "added", 0, null))).Should().Contain("too big, or is not text");
        ChangePolicy.Refusal(Change(File("data.bin", "added", 40, null))).Should().Contain("too big, or is not text");
        ChangePolicy.Refusal(Change(File("package-lock.json", "modified", 5000, null))).Should().Contain("too big, or is not text", "nobody could read it");
        ChangePolicy.Refusal(Change(File("big.txt", "modified", 1, new string('x', ChangePolicy.MaxFilePatch + 1)))).Should().Contain("too big");
    }

    [Theory]
    [InlineData("package-lock.json")]
    [InlineData("frontend/yarn.lock")]
    [InlineData("pnpm-lock.yaml")]
    public void A_lock_file_may_only_name_registries_where_it_says_that_a_package_comes_from(string lockFile)
    {
        const string good = "@@\n+      \"resolved\": \"https://registry.npmjs.org/lodash/-/lodash-4.18.1.tgz\",\n+      \"funding\": { \"url\": \"https://github.com/sponsors/x\" }";
        ChangePolicy.Refusal(Change(File(lockFile, "modified", 3, good))).Should().BeNull("a funding link is not where a package is fetched from");
        ChangePolicy.Refusal(Change(File(lockFile, "modified", 3, "@@\n+      \"resolved\": \"https://evil.example/lodash.tgz\","))).Should().Contain("evil.example");
        ChangePolicy.Refusal(Change(File(lockFile, "modified", 3, "@@\n+      \"resolved\": \"git+ssh://git@github.com/someone/lodash.git#abc\","))).Should().Contain("github.com");
        ChangePolicy.Refusal(Change(File(lockFile, "modified", 3, "@@\n-      \"resolved\": \"https://evil.example/old.tgz\",\n+      \"resolved\": \"https://registry.npmjs.org/a/-/a-1.tgz\","))).Should().BeNull("a line that is removed is not where anything comes from now");
    }

    [Theory]
    [InlineData("Cargo.lock", "@@\n+source = \"git+https://evil.example/crate#abc\"")]
    [InlineData("poetry.lock", "@@\n+url = \"https://evil.example/pkg.whl\"")]
    [InlineData("package-lock.json", "@@\n+      \"resolved\": \"https://registry.npmjs.org.evil.example/a.tgz\",")]
    [InlineData("pnpm-lock.yaml", "@@\n+      tarball: https://evil.example/a.tgz")]
    [InlineData("yarn.lock", "@@\n+  resolution: \"lodash@https://evil.example/lodash.tgz\"")]
    [InlineData("package-lock.json", "@@\n+      \"resolved\": \"https://user@evil.example:8443/a.tgz\",")]
    public void A_lock_file_of_any_ecosystem_that_adds_an_address_that_is_not_a_registry_is_refused(string lockFile, string patch)
    {
        ChangePolicy.Refusal(Change(File(lockFile, "added", 3, patch))).Should().Contain("not a registry");
    }

    [Fact]
    public void A_change_that_the_controller_could_not_read_in_full_is_refused_so_that_it_is_made_smaller()
    {
        var wide = Enumerable.Range(0, 41).Select(index => File($"src/f{index}.js")).ToArray();
        var long_ = File("src/big.js", "modified", 1, "@@\n" + string.Join('\n', Enumerable.Range(0, 400).Select(line => $"+{new string('x', 40)}{line}")));

        ChangePolicy.Refusal(Change(wide)).Should().Contain("too large for the controller");
        ChangePolicy.Refusal(Change(long_)).Should().Contain("too large for the controller");
        ChangePolicy.Refusal(Change([.. Enumerable.Range(0, 40).Select(index => File($"src/f{index}.js"))])).Should().BeNull("forty files that are read in full are fine");
    }

    [Fact]
    public void A_generated_file_is_one_that_a_tool_writes_by_its_name_not_by_the_end_of_it()
    {
        ChangePolicy.IsGenerated("package-lock.json").Should().BeTrue();
        ChangePolicy.IsGenerated("a/b/poetry.lock").Should().BeTrue();
        ChangePolicy.IsGenerated("notapackage-lock.json").Should().BeFalse();
        ChangePolicy.IsGenerated("src/unlock.json").Should().BeFalse();
    }

    [Fact]
    public void The_total_size_of_the_patches_is_bounded()
    {
        var files = Enumerable.Range(0, 8).Select(index => File($"f{index}.txt", "modified", 1, new string('x', 600_000))).ToArray();

        ChangePolicy.Refusal(Change(files)).Should().Contain("too big");
    }

    [Fact]
    public void The_controller_reads_the_size_of_every_file_and_the_lines_of_those_that_a_tool_did_not_write_and_a_cut_is_said()
    {
        var reading = ChangePolicy.Reading(Change(
            File("package.json", patch: "@@ -1 +1 @@\n-\"lodash\": \"4.17.4\"\n+\"lodash\": \"4.18.1\""),
            File("package-lock.json", "modified", 900, "@@\n-x\n+y")));

        reading.Should().Contain("package.json (modified, 2 lines)").And.Contain("package-lock.json").And.Contain("\"lodash\": \"4.18.1\"").And.Contain("Generated files changed too (their lines are not shown, the control plane checked that every address in them is a registry): package-lock.json");
        reading.Should().NotContain("[CUT");
        ChangePolicy.Reading(Change([.. Enumerable.Range(0, 41).Select(index => File($"f{index}.txt"))])).Should().Contain("[CUT: the rest is not shown]");
    }

    [Fact]
    public void A_floor_path_of_the_frame_that_this_policy_does_not_name_is_refused_too()
    {
        ChangePolicy.IsProtected("CODEOWNERS").Should().BeTrue();
        ChangePolicy.IsProtected("src/server.js").Should().BeFalse();
    }
}

public sealed class AgentBranchTests
{
    [Theory]
    [InlineData("p-4afd49f588f8", "agent/p-4afd49f588f8-")]
    [InlineData("P_X/../y", "agent/p-x-y-")]
    [InlineData("-lead", "agent/lead-")]
    [InlineData("!!!", "agent/p-")]
    public void A_branch_is_named_after_its_problem_made_plain(string problemId, string prefix)
    {
        AgentBranch.PrefixOf(problemId).Should().Be(prefix);
        AgentBranch.IsName(AgentBranch.Create(problemId, "1a2b3c")).Should().BeTrue();
    }

    [Fact]
    public void Even_a_very_long_problem_gives_a_name_that_the_gateway_accepts_and_a_branch_belongs_to_one_problem_only()
    {
        var name = AgentBranch.Create(new string('x', 200), "1a2b3c");

        AgentBranch.IsName(name).Should().BeTrue();
        AgentBranch.Belongs(name, new string('x', 200)).Should().BeTrue();
        AgentBranch.Belongs(AgentBranch.Create("p-1", "aaaaaa"), "p-2").Should().BeFalse("another problem's branch is not audited for this one");
        AgentBranch.IsName("main").Should().BeFalse();
        AgentBranch.IsName("agent/../x").Should().BeFalse();
        AgentBranch.IsName(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("FROM python:3.7", "FROM python:3.8", null)]
    [InlineData("FROM python:3.7 AS b\nFROM b", "FROM python:3.8 AS b\nFROM b", null)]
    [InlineData("FROM python:3.7", "FROM python:3.8 AS base\nFROM base", null)]
    [InlineData("FROM python:3.7", "FROM docker.io/library/python:3.8", null)]
    [InlineData("FROM python:3.7", "FROM golang:1.23 AS build\nFROM python:3.8", null)]
    [InlineData("FROM python:3.7", "# syntax=docker/dockerfile:1\nFROM python:3.8\n# a comment\nCOPY --from=build /x /", null)]
    [InlineData("FROM python:3.7", "FROM\tevil/python:3.12", "run on 'evil/python' instead of 'python'")]
    [InlineData("FROM python:3.7", "FROM python:3.7 AS unused\nFROM evil/img", "run on 'evil/img'")]
    [InlineData("FROM python:3.7", "FROM python:3.8\nFROM node:20 AS assets", "run on 'node'")]
    [InlineData("FROM python:3.7", "FROM evil/img AS python\nFROM python", "run on 'evil/img'")]
    [InlineData("ARG BASE=python:3.7\nFROM ${BASE}", "ARG BASE=python:3.7\nFROM ${BASE}\nARG BASE=evil/img", null)]
    [InlineData("ARG BASE=python:3.7\nFROM ${BASE}", "ARG BASE=python:3.8\nFROM ${BASE}", null)]
    [InlineData("ARG BASE=\"python:3.7\"\nFROM ${BASE}", "ARG BASE=\"python:3.8\"\nFROM ${BASE}", null)]
    [InlineData("ARG BASE=python:3.7\nFROM ${BASE}", "ARG BASE=ghcr.io/someone/python:3.12\nFROM ${BASE}", "run on 'ghcr.io/someone/python'")]
    [InlineData("ARG BASE=python:3.7\nFROM ${BASE}", "ARG BASE=python:3.7\nARG X=1 BASE=evil/img\nFROM ${BASE}", "run on 'evil/img'")]
    [InlineData("ARG BASE=python:3.7\nFROM ${BASE}", "ARG BASE=python:3.7 \\\n  BASE=evil/img\nFROM ${BASE}", "run on 'evil/img'")]
    [InlineData("ARG BASE=python:3.7\nFROM ${BASE}", "ARG BASE=\"evil/img x\"\nFROM ${BASE}", "cannot be told from the file")]
    [InlineData("FROM python:3.7", "FROM \\\n  evil/img", "run on 'evil/img'")]
    [InlineData("FROM python:3.7", "ARG BASE\nFROM ${BASE:-node:20}", "run on 'node'")]
    [InlineData("FROM python:3.7", "ARG BASE=node\nFROM ${BASE}", "run on 'node'")]
    [InlineData("FROM python:3.7", "FROM ${BASE}", "cannot be told from the file")]
    [InlineData("FROM python:3.7", "# escape=`\nFROM python:3.8", "escape directive")]
    [InlineData("FROM python:3.7", "FROM evil:1\nRUN true \\\n# note\nFROM python:3.7", "run on 'evil'")]
    [InlineData("FROM python:3.7", "FROM evil:1\nRUN true \\\n\nFROM python:3.7", "run on 'evil'")]
    [InlineData("FROM python:3.7", "FROM evil:1\nCOPY <<EOF /x\nFROM python:3.7\nEOF", "cannot be told from the file")]
    [InlineData("ARG URL=https://example.test/tool-1.2.tgz\nFROM python:3.7", "ARG URL=https://example.test/tool-1.3.tgz\nFROM python:3.8", null)]
    public void A_base_image_is_read_on_the_whole_file_with_the_defaults_of_its_args(string before, string after, string? refusal)
    {
        var verdict = ChangePolicy.SwappedBaseImage(new Orchestrarr.Core.Forge.BranchFile("Dockerfile", "modified", 2, "@@ -1 +1 @@"), before, after);

        if (refusal is null)
        {
            verdict.Should().BeNull();
        }
        else
        {
            verdict.Should().Contain(refusal);
        }
    }

    [Theory]
    [InlineData("Dockerfile")]
    [InlineData("dockerfile")]
    [InlineData("Containerfile")]
    [InlineData("build/app.dockerfile")]
    public void A_container_file_that_was_not_read_whole_is_refused_and_every_container_file_is_read(string path)
    {
        var change = new Orchestrarr.Core.Forge.BranchChange("0123456789abcdef0123456789abcdef01234567", 0, 1, [new Orchestrarr.Core.Forge.BranchFile(path, "modified", 2, "@@ -1 +1 @@\n-FROM python:3.7\n+FROM python:3.8")], true);
        ChangePolicy.Refusal(change).Should().Contain("could not be read whole", "the check fails closed");
        ChangePolicy.Refusal(change, null, new Dictionary<string, string?> { [path] = "FROM python:3.7" }, new Dictionary<string, string?> { [path] = "FROM python:3.8" }).Should().BeNull("the fix of CASE-06 passes");
        ChangePolicy.Refusal(change, null, new Dictionary<string, string?> { [path] = "FROM python:3.7" }, new Dictionary<string, string?> { [path] = "FROM evil/python:3.12" }).Should().Contain("instead of 'python'");
        ChangePolicy.IsContainerFile("compose.yaml").Should().BeFalse();
    }

    [Fact]
    public void A_new_container_file_is_read_alone_a_removed_one_is_not_judged_and_a_modified_one_without_its_text_before_is_refused()
    {
        ChangePolicy.SwappedBaseImage(new Orchestrarr.Core.Forge.BranchFile("Dockerfile", "added", 2, null), null, "FROM python:3.8").Should().BeNull("a new container file is the controller's to judge on the patch");
        ChangePolicy.SwappedBaseImage(new Orchestrarr.Core.Forge.BranchFile("Dockerfile", "added", 2, null), null, "FROM ${BASE}").Should().Contain("cannot be told from the file");
        ChangePolicy.SwappedBaseImage(new Orchestrarr.Core.Forge.BranchFile("Dockerfile", "added", 2, null), "FROM python:3.7", "FROM evil/img").Should().Contain("run on 'evil/img'", "a path that existed at the merge base is compared, whatever the forge calls it");
        ChangePolicy.SwappedBaseImage(new Orchestrarr.Core.Forge.BranchFile("Dockerfile", "removed", 0, null), "FROM python:3.7", null).Should().BeNull();
        ChangePolicy.SwappedBaseImage(new Orchestrarr.Core.Forge.BranchFile("Dockerfile", "modified", 2, null), null, "FROM python:3.8").Should().Contain("could not be read whole");
        ChangePolicy.SwappedBaseImage(new Orchestrarr.Core.Forge.BranchFile("Dockerfile", "modified", 2, null), "FROM python:3.7", null).Should().Contain("could not be read whole");
    }
}
