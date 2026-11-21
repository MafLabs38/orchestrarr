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

    public static int Of(string repository, int basePort) => basePort + BitConverter.ToUInt16(SHA256.HashData(Encoding.UTF8.GetBytes(repository)), 0) % Span;
}
