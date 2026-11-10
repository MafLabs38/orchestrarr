using Orchestrarr.Core.Settings;

namespace Orchestrarr.Host.Settings;

/// <summary>
/// The leave the host gives to lower its own security from the screen (ADR 0023): off unless it is started with it, and never in production. It
/// is read from the configuration at the start and nothing the screen does can change it. It is an allow-list: only a host that says it is a
/// development or test host can have it, so a Staging or a custom environment name is refused like Production.
/// </summary>
public sealed class SecurityOptionsConfiguration
{
    public const string SectionName = "Options";

    /// <summary>Lets an administrator turn off an option that lowers security (the authenticator code) from the screen, for development and tests.</summary>
    public bool AllowRelaxingSecurity { get; set; }

    private static readonly string[] Environments = ["Development", "Testing"];

    public OptionsSettings Validate(string environmentName)
    {
        if (AllowRelaxingSecurity && !Environments.Contains(environmentName, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Options:AllowRelaxingSecurity is for the Development and Testing environments, not '{environmentName}': in a deployment the approvals carry the code of the authenticator and nothing can turn it off.");
        }
        return new OptionsSettings(AllowRelaxingSecurity);
    }
}
