using System.Security.Cryptography;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Agents;
using Orchestrarr.Host.Storage;
using Orchestrarr.Storage;

namespace Orchestrarr.Host.Missions;

/// <summary>Opens the signing key of the control plane: kept in a file for its owner only when there is a place, else made for this run.</summary>
internal static class SigningKey
{
    public static MandateSigner Open(string? file)
    {
        if (file is null)
        {
            return MandateSigner.Create();
        }
        if (File.Exists(file))
        {
            return MandateSigner.FromPrivateKey(File.ReadAllBytes(file));
        }
        var signer = MandateSigner.Create();
        var folder = Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".";
        Directory.CreateDirectory(folder);
        // Written whole under a name of its own, for its owner only from the first byte, then moved into place without replacing: a start that comes second, or one over a half-written file, never reads a truncated key
        var pending = Path.Combine(folder, $".{Path.GetFileName(file)}.{Guid.NewGuid():N}.pending");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var stream = new FileStream(pending, options))
        {
            stream.Write(signer.PrivateKey());
        }
        try
        {
            File.Move(pending, file, overwrite: false);
            return signer;
        }
        catch (IOException)
        {
            // Another start put its key there first: the one in place is the key of the control plane
            File.Delete(pending);
            signer.Dispose();
            return MandateSigner.FromPrivateKey(File.ReadAllBytes(file));
        }
    }
}
