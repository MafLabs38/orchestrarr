namespace Orchestrarr.Core.Tasks;

/// <summary>A repository pattern as the executors and the table read it: <c>*</c> stands for any characters, names are compared without regard to case.</summary>
public static class RepositoryPattern
{
    public static bool Matches(string pattern, string repository)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(repository);
        var parts = pattern.Split('*');
        var position = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i == 0)
            {
                if (!repository.StartsWith(part, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                position = part.Length;
                continue;
            }
            if (i == parts.Length - 1)
            {
                return repository.Length - position >= part.Length && repository.EndsWith(part, StringComparison.OrdinalIgnoreCase);
            }
            var found = repository.IndexOf(part, position, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }
            position = found + part.Length;
        }
        return position == repository.Length;
    }
}
