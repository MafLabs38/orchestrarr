using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.SecondFactor;
using TotpMath = Orchestrarr.Core.SecondFactor.Totp;

namespace Orchestrarr.Core.Test.SecondFactor;

public sealed class SecondFactorGuardTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 12, 0, 10, TimeSpan.Zero);
    private static readonly byte[] Secret = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
    private static readonly TotpOptions Shape = new();

    private readonly FakeTimeProvider _time = new(Start);
    private readonly ISecondFactorSecrets _secrets = Substitute.For<ISecondFactorSecrets>();
    private readonly SecondFactorGuard _guard;

    public SecondFactorGuardTests()
    {
        _secrets.FindAsync("alice", Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<byte[]?>(Secret));
        _secrets.FindAsync(Arg.Is<string>(s => s != "alice"), Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<byte[]?>(null));
        _guard = new SecondFactorGuard(_secrets, new InMemorySecondFactorUseLedger(), _time, new SecondFactorOptions());
    }

    private string CodeAt(int stepOffset = 0) => TotpMath.Code(Secret, TotpMath.StepAt(_time.GetUtcNow(), Shape) + stepOffset, Shape);

    private ValueTask<SecondFactorResult> Verify(string? code, string subject = "alice") => _guard.VerifyAsync(subject, code, CancellationToken.None);

    private static string Wrong(string right) => right == "000000" ? "000001" : "000000";

    [Fact]
    public async Task The_right_code_of_an_enrolled_operator_is_verified()
    {
        (await Verify(CodeAt())).Should().Be(SecondFactorResult.Verified);
    }

    [Fact]
    public async Task Spaces_in_the_code_are_ignored()
    {
        var code = CodeAt();

        (await Verify($"{code[..3]} {code[3..]}")).Should().Be(SecondFactorResult.Verified);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("١٢٣٤٥٦")]
    public async Task A_code_that_is_not_six_digits_is_malformed(string? code)
    {
        (await Verify(code)).Should().Be(SecondFactorResult.Malformed);
    }

    [Fact]
    public async Task A_wrong_code_is_refused()
    {
        (await Verify(Wrong(CodeAt(0)))).Should().Be(SecondFactorResult.Wrong);
    }

    [Fact]
    public async Task An_operator_without_a_secret_is_refused_like_a_wrong_code()
    {
        (await Verify(CodeAt(), "mallory")).Should().Be(SecondFactorResult.UnknownSubject);
    }

    [Fact]
    public async Task A_code_is_good_once()
    {
        var code = CodeAt();

        (await Verify(code)).Should().Be(SecondFactorResult.Verified);
        (await Verify(code)).Should().Be(SecondFactorResult.Reused);
    }

    [Fact]
    public async Task An_older_code_is_refused_after_a_newer_one_was_accepted()
    {
        var older = CodeAt(-1);
        var newer = CodeAt(0);

        (await Verify(newer)).Should().Be(SecondFactorResult.Verified);
        (await Verify(older)).Should().Be(SecondFactorResult.Reused);
    }

    [Fact]
    public async Task The_next_code_works_after_the_step_has_passed()
    {
        (await Verify(CodeAt())).Should().Be(SecondFactorResult.Verified);

        _time.Advance(TimeSpan.FromSeconds(30));

        (await Verify(CodeAt())).Should().Be(SecondFactorResult.Verified);
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_operator_out_even_for_the_right_code()
    {
        for (var i = 0; i < 5; i++)
        {
            (await Verify(Wrong(CodeAt()))).Should().Be(SecondFactorResult.Wrong);
        }

        (await Verify(CodeAt())).Should().Be(SecondFactorResult.LockedOut);
    }

    [Fact]
    public async Task The_lockout_ends_and_the_count_starts_again()
    {
        for (var i = 0; i < 5; i++)
        {
            await Verify(Wrong(CodeAt()));
        }

        _time.Advance(TimeSpan.FromMinutes(16));

        (await Verify(CodeAt())).Should().Be(SecondFactorResult.Verified);
        // One failure after a lockout is one failure, not the sixth
        for (var i = 0; i < 4; i++)
        {
            (await Verify(Wrong(CodeAt()))).Should().Be(SecondFactorResult.Wrong);
        }
    }

    [Fact]
    public async Task Failures_older_than_the_window_are_forgotten()
    {
        for (var i = 0; i < 4; i++)
        {
            await Verify(Wrong(CodeAt()));
        }

        _time.Advance(TimeSpan.FromMinutes(20));

        (await Verify(Wrong(CodeAt()))).Should().Be(SecondFactorResult.Wrong);
        (await Verify(CodeAt())).Should().Be(SecondFactorResult.Verified);
    }

    [Fact]
    public async Task A_right_code_clears_the_failures()
    {
        for (var i = 0; i < 4; i++)
        {
            await Verify(Wrong(CodeAt()));
        }
        await Verify(CodeAt());
        _time.Advance(TimeSpan.FromSeconds(30));

        for (var i = 0; i < 4; i++)
        {
            (await Verify(Wrong(CodeAt()))).Should().Be(SecondFactorResult.Wrong);
        }
    }

    [Fact]
    public async Task One_operator_locked_out_does_not_lock_another()
    {
        _secrets.FindAsync("bob", Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult<byte[]?>(Secret));
        for (var i = 0; i < 5; i++)
        {
            await Verify(Wrong(CodeAt()));
        }

        (await Verify(CodeAt(), "bob")).Should().Be(SecondFactorResult.Verified);
    }

    [Fact]
    public async Task Made_up_names_do_not_grow_the_memory_without_bound()
    {
        var guard = new SecondFactorGuard(_secrets, new InMemorySecondFactorUseLedger(), _time, new SecondFactorOptions { TrackedSubjects = 3 });

        for (var i = 0; i < 20; i++)
        {
            (await guard.VerifyAsync($"invented-{i}", "123456", CancellationToken.None)).Should().Be(SecondFactorResult.UnknownSubject);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        // Still working for a real operator
        (await guard.VerifyAsync("alice", CodeAt(), CancellationToken.None)).Should().Be(SecondFactorResult.Verified);
    }

    [Fact]
    public async Task Made_up_names_cannot_push_a_real_lockout_out_of_the_table()
    {
        var guard = new SecondFactorGuard(_secrets, new InMemorySecondFactorUseLedger(), _time, new SecondFactorOptions { TrackedSubjects = 3 });
        for (var i = 0; i < 5; i++)
        {
            (await guard.VerifyAsync("alice", Wrong(CodeAt()), CancellationToken.None)).Should().Be(SecondFactorResult.Wrong);
        }

        for (var i = 0; i < 20; i++)
        {
            await guard.VerifyAsync($"invented-{i}", "123456", CancellationToken.None);
        }

        (await guard.VerifyAsync("alice", CodeAt(), CancellationToken.None)).Should().Be(SecondFactorResult.LockedOut);
    }

    [Fact]
    public async Task Two_requests_with_the_same_code_never_both_succeed()
    {
        var code = CodeAt();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Verify(code).AsTask()));

        results.Count(r => r == SecondFactorResult.Verified).Should().Be(1);
    }
}
