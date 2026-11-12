using Orchestrarr.Core.SecondFactor;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor.Test;

public sealed class OperatorCommandsTests : IDisposable
{
    private readonly ExecutorFixture _world = new();
    private readonly FakeIo _io = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _world.Dispose();

    private OperatorCommands Commands() => new(_world.Store, _world.Guard, ExecutorFixture.Totp, "Orchestrarr", _world.Time, _io);

    private sealed class FakeIo : IConsoleIo
    {
        public List<string> Lines { get; } = [];

        public Queue<string> Secrets { get; } = new();

        public void WriteLine(string text) => Lines.Add(text);

        public string ReadSecret(string prompt) => Secrets.Count > 0 ? Secrets.Dequeue() : string.Empty;

        public string All => string.Join('\n', Lines);
    }

    [Fact]
    public async Task Enrolling_shows_a_code_to_scan_and_the_key_and_enrols_nobody_without_a_confirming_code()
    {
        var result = await Commands().EnrolAsync("bob", replace: false, Ct);

        result.Should().Be(1, "no code was given, so nothing is enrolled");
        _io.All.Should().Contain("Scan this").And.Contain("Do not copy this screen").And.Contain("Key: ").And.Contain("nothing was enrolled");
        (await _world.Store.FindAsync("bob", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_right_code_enrols_the_operator_and_spends_that_code()
    {
        var io = new ConfirmingIo(_world);
        var commands = new OperatorCommands(_world.Store, _world.Guard, ExecutorFixture.Totp, "Orchestrarr", _world.Time, io);

        (await commands.EnrolAsync("bob", replace: false, Ct)).Should().Be(0);

        (await _world.Store.FindAsync("bob", Ct)).Should().NotBeNull();
        io.Lines.Should().Contain("bob is enrolled.");
        (await _world.Guard.VerifyAsync("bob", io.CodeGiven, Ct)).Should().Be(SecondFactorResult.Reused, "the code that confirmed the secret is spent");
    }

    [Fact]
    public async Task An_operator_already_enrolled_is_not_replaced_without_asking()
    {
        (await Commands().EnrolAsync("alice", replace: false, Ct)).Should().Be(1);

        _io.All.Should().Contain("already enrolled");
        (await _world.Store.FindAsync("alice", Ct)).Should().Equal(ExecutorFixture.Secret);
    }

    [Fact]
    public async Task A_subject_with_odd_characters_is_refused()
    {
        (await Commands().EnrolAsync("bob; drop table", replace: false, Ct)).Should().Be(2);
    }

    [Fact]
    public async Task A_delegation_is_added_only_with_the_right_code_of_the_operator()
    {
        _io.Secrets.Enqueue("000000");
        (await Commands().AddDelegationAsync("alice", TaskKind.ValidatePullRequest, "example/*", 30, 5, Ct)).Should().Be(1);
        _world.Store.Delegations(includeEnded: true).Should().BeEmpty();

        _io.Secrets.Enqueue(_world.CodeAt());
        (await Commands().AddDelegationAsync("alice", TaskKind.ValidatePullRequest, "example/*", 30, 5, Ct)).Should().Be(0);

        var delegation = _world.Store.Delegations(includeEnded: true).Should().ContainSingle().Subject;
        delegation.Should().Match<Delegation>(d => d.Subject == "alice" && d.Kind == TaskKind.ValidatePullRequest && d.Repository == "example/*" && d.DailyQuota == 5);
        delegation.ExpiresAt.Should().Be(ExecutorFixture.Start.AddDays(30));
    }

    [Theory]
    [InlineData(0, 5, "example/*")]
    [InlineData(91, 5, "example/*")]
    [InlineData(30, 0, "example/*")]
    [InlineData(30, 5, "")]
    [InlineData(30, 5, "example/app; rm")]
    [InlineData(30, 5, "*")]
    [InlineData(30, 5, "*/*")]
    [InlineData(30, 5, "**")]
    [InlineData(30, 5, "ex*/app")]
    [InlineData(30, 5, "*/app")]
    public async Task A_delegation_outside_the_frame_is_refused_before_any_code_is_asked(int days, int quota, string repository)
    {
        (await Commands().AddDelegationAsync("alice", TaskKind.ValidatePullRequest, repository, days, quota, Ct)).Should().Be(2);
    }

    [Fact]
    public async Task A_repair_cannot_be_delegated_because_it_spends_the_subscription()
    {
        (await Commands().AddDelegationAsync("alice", TaskKind.RepairPullRequest, "example/*", 30, 5, Ct)).Should().Be(2);

        _world.Store.Delegations(includeEnded: true).Should().BeEmpty();
    }

    [Fact]
    public async Task A_merge_cannot_be_delegated_yet()
    {
        (await Commands().AddDelegationAsync("alice", TaskKind.MergePullRequest, "example/*", 30, 5, Ct)).Should().Be(2);

        _world.Store.Delegations(includeEnded: true).Should().BeEmpty();
    }

    [Fact]
    public async Task A_delegation_is_revoked_with_the_code_and_listed_as_such()
    {
        _io.Secrets.Enqueue(_world.CodeAt());
        await Commands().AddDelegationAsync("alice", TaskKind.ValidatePullRequest, "example/*", 30, 5, Ct);
        var id = _world.Store.Delegations(includeEnded: true)[0].Id;
        _world.Time.Advance(TimeSpan.FromSeconds(30));

        _io.Secrets.Enqueue(_world.CodeAt());
        (await Commands().RevokeDelegationAsync("alice", id, Ct)).Should().Be(0);

        Commands().ListDelegations().Should().Be(0);
        _io.All.Should().Contain("revoked").And.Contain(id.ToString("D"));
    }

    [Fact]
    public async Task Giving_and_revoking_a_delegation_leave_a_trace_of_who_confirmed_and_what_it_was()
    {
        _io.Secrets.Enqueue(_world.CodeAt());
        await Commands().AddDelegationAsync("alice", TaskKind.ValidatePullRequest, "example/*", 30, 5, Ct);
        var id = _world.Store.Delegations(includeEnded: true)[0].Id;
        _world.Time.Advance(TimeSpan.FromSeconds(30));
        _io.Secrets.Enqueue(_world.CodeAt());
        await Commands().RevokeDelegationAsync("alice", id, Ct);

        var acts = _world.Store.Acts(10);

        acts.Select(act => act.Act).Should().Equal("delegation-revoked", "delegation-added");
        acts.Should().OnlyContain(act => act.Subject == "alice");
        acts[1].Detail.Should().Contain(id.ToString("D")).And.Contain("example/*");
        Commands().ListActs().Should().Be(0);
        _io.All.Should().Contain("delegation-added");
    }

    [Fact]
    public void Nothing_to_list_says_so()
    {
        Commands().ListDelegations();
        new OperatorCommands(new ExecutorStore(Path.Combine(_world.Folder, "empty.db")), _world.Guard, ExecutorFixture.Totp, "Orchestrarr", _world.Time, _io).ListOperators();

        _io.All.Should().Contain("No delegation.").And.Contain("No operator is enrolled.");
    }

    [Fact]
    public void The_code_to_scan_is_drawn_with_block_characters_and_is_not_empty()
    {
        var drawing = OperatorCommands.Qr("otpauth://totp/Orchestrarr:alice?secret=MZXW6YTBOI");

        drawing.Should().ContainAny("█", "▀", "▄");
        drawing.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.Should().BeGreaterThan(10);
    }

    // Answers the confirmation with the right code for the secret that the command just made, read from the screen
    private sealed class ConfirmingIo(ExecutorFixture world) : IConsoleIo
    {
        public List<string> Lines { get; } = [];

        public string CodeGiven { get; private set; } = string.Empty;

        public void WriteLine(string text) => Lines.Add(text);

        private static string KeyIn(string output)
        {
            var line = output.Split('\n').Single(l => l.StartsWith("Key: ", StringComparison.Ordinal));
            Base32.TryDecode(line[5..], out var secret).Should().BeTrue();
            return Convert.ToHexString(secret);
        }

        public string ReadSecret(string prompt)
        {
            var secret = KeyIn(string.Join('\n', Lines));
            CodeGiven = SecondFactor.TotpMath.Code(Convert.FromHexString(secret), SecondFactor.TotpMath.StepAt(world.Time.GetUtcNow(), ExecutorFixture.Totp), ExecutorFixture.Totp);
            return CodeGiven;
        }
    }
}
