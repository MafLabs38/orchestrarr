using Orchestrarr.Core.Settings;

namespace Orchestrarr.Host.Settings;

/// <summary>
/// The leave the host gives to lower its own security from the screen (ADR 0023): off unless it is started with it, and never in production. It
/// is read from the configuration at the start and nothing the screen does can change it.
/// </summary>
public sealed class SecurityOptionsConfiguration
{
    public const string SectionName = "Options";

    /// <summary>Lets an administrator turn off an option that lowers security (the authenticator code) from the screen, for development and tests.</summary>
    public bool AllowRelaxingSecurity { get; set; }

    public OptionsSettings Validate(bool production)
    {
        if (AllowRelaxingSecurity && production)
        {
            throw new InvalidOperationException("Options:AllowRelaxingSecurity is for development and tests: in production the approvals carry the code of the authenticator and nothing can turn it off.");
        }
        return new OptionsSettings(AllowRelaxingSecurity);
    }
}
