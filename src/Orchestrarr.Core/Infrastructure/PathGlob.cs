using System.Text.RegularExpressions;

namespace Orchestrarr.Core.Infrastructure;

/// <summary>
/// The paths of a repository that the description names: <c>*</c> is any characters inside one folder name, <c>**</c> any folders (or none), <c>?</c> one character.
/// A file that holds secrets is never read, whatever a pattern says (ADR 0028).
/// </summary>
public static class PathGlob
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Compiled = new(StringComparer.Ordinal);
    private static readonly string[] SecretNames = [".enc.", ".tfstate", ".tfvars", ".pem", ".key", "id_rsa", "id_ed25519", "secret"];

    /// <summary>True when the path is matched by the pattern.</summary>
    public static bool IsMatch(string pattern, string path)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(path);
        return Compiled.GetOrAdd(pattern, ToRegex).IsMatch(path);
    }

    /// <summary>True for a file whose name says it holds a secret or the state of an infrastructure: a pattern does not make it readable.</summary>
    public static bool IsSecretFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();
        // ".env" is a file of the environment (".env", ".env.local", "prod.env"), not any name that contains the letters
        return name == ".env" || name.StartsWith(".env.", StringComparison.Ordinal) || name.EndsWith(".env", StringComparison.Ordinal) || SecretNames.Any(part => name.Contains(part, StringComparison.Ordinal));
    }

    // "**/" is any folders or none, a trailing "**" anything, "*" a name, "?" a character: the tokens are replaced one after the other, so that no index is moved by hand
    private static Regex ToRegex(string pattern)
    {
        const string AnyFolders = "\u0001";
        const string Anything = "\u0002";
        const string Name = "\u0003";
        const string Character = "\u0004";
        var marked = pattern.Replace("**/", AnyFolders, StringComparison.Ordinal).Replace("**", Anything, StringComparison.Ordinal)
            .Replace("*", Name, StringComparison.Ordinal).Replace("?", Character, StringComparison.Ordinal);
        var escaped = Regex.Escape(marked)
            .Replace(AnyFolders, "(?:.*/)?", StringComparison.Ordinal).Replace(Anything, ".*", StringComparison.Ordinal)
            .Replace(Name, "[^/]*", StringComparison.Ordinal).Replace(Character, "[^/]", StringComparison.Ordinal);
        return new Regex($"^{escaped}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    }
}
