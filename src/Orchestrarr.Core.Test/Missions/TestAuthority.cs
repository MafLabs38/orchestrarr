using Orchestrarr.Core.Missions;
using Orchestrarr.Core.SecondFactor;

namespace Orchestrarr.Core.Test.Missions;

/// <summary>A real approval authority with a real second factor, for tests: each approval is made by a different operator name so that one code is never used twice.</summary>
internal sealed class TestAuthority : IApprovalSource, IDisposable
{
    private readonly byte[] _secret = OtpAuthUri.NewSecret();
    private readonly TimeProvider _time;
    private int _operators;

    public TestAuthority(TimeProvider time)
    {
        _time = time;
        Authority = new ApprovalAuthority(new SecondFactorGuard(new Secrets(_secret), new InMemorySecondFactorUseLedger(), time, new SecondFactorOptions()), time, () => Guid.NewGuid().ToString("N"));
        Verifier = new ApprovalVerifier(Authority.PublicKey(), time);
    }

    public ApprovalAuthority Authority { get; }

    public ApprovalVerifier Verifier { get; }

    /// <summary>False: no certificate is given for any entry.</summary>
    public bool Issue { get; set; } = true;

    public CertificateKind Kind { get; set; } = CertificateKind.Problem;

    /// <summary>Changes what is asked of the authority, to make a certificate narrower or different from the plan entry.</summary>
    public Func<ApprovalRequest, ApprovalRequest>? Adjust { get; set; }

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(2);

    public string CodeFor() => Totp.Code(_secret, Totp.StepAt(_time.GetUtcNow(), new TotpOptions()), new TotpOptions());

    public (SignedApproval? Approval, SecondFactorResult Result) Approve(ApprovalRequest request, string? code = null)
    {
        var operatorName = $"alice-{Interlocked.Increment(ref _operators)}";
        return Authority.ApproveAsync(request, operatorName, code ?? CodeFor(), CancellationToken.None).GetAwaiter().GetResult();
    }

    public SignedApproval? For(PlanEntry entry)
    {
        if (!Issue)
        {
            return null;
        }
        var subject = Kind == CertificateKind.Problem ? entry.ProblemId : entry.ProblemClass;
        var request = new ApprovalRequest(Kind, subject, entry.ApplicationId, entry.Repositories, entry.MaxWorld, entry.Budget, Lifetime);
        return Approve(Adjust?.Invoke(request) ?? request).Approval;
    }

    public void Dispose()
    {
        Authority.Dispose();
        Verifier.Dispose();
    }

    private sealed class Secrets(byte[] secret) : ISecondFactorSecrets
    {
        // Every operator name of the test has the same secret: the point is the authority's checks, not the enrolment
        public ValueTask<byte[]?> FindAsync(string subject, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(secret);
    }
}
