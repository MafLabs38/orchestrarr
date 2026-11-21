using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Orchestrarr.Core.Missions;

/// <summary>
/// Where a deployed application of the test bed listens: a port that is stable for a repository, from a base port. One function, read by the runner that deploys and by the control plane that looks at the result (rule 14), so that
/// the control plane reads the health of what was deployed from the settings and never from the worker's word.
/// </summary>
public static class DeploymentPort
{
    public const int Span = 100;

    /// <summary>The base that both sides use unless they are told another: the control plane and the runner have to agree on it.</summary>
    public const int DefaultBase = 8100;

    /// <summary>Whether a base port leaves room for the whole span below the last port.</summary>
    public static bool IsValidBase(int basePort) => basePort is >= 1024 && basePort + Span <= 65535;

    // Big-endian whatever the machine is: the runner and the control plane may not run on the same architecture
    public static int Of(string repository, int basePort) => basePort + BinaryPrimitives.ReadUInt16BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(repository))) % Span;
}
