namespace Orchestrarr.Core.Settings;

/// <summary>The options an operator can switch (ADR 0023). Each one is a yes or a no.</summary>
public static class OptionIds
{
    /// <summary>Approving asks for the code of the authenticator. Turning it off lowers security: it is for development and tests.</summary>
    public const string RequireSecondFactor = "approvals.require-second-factor";

    public static IReadOnlyList<string> All { get; } = [RequireSecondFactor];
}

/// <summary>What the host allows, fixed when it starts: the screen cannot widen it.</summary>
public sealed record OptionsSettings(bool AllowRelaxingSecurity)
{
    /// <summary>The default: nothing that lowers security can be turned off from the screen.</summary>
    public static OptionsSettings Locked { get; } = new(false);
}

/// <summary>Where an option is kept. A read that fails is an error here, and the service reads it as the safe value.</summary>
public interface IOptionStore
{
    /// <summary>The value written for this option, or null when none was written.</summary>
    bool? Get(string id);

    /// <summary>Writes the value. False when it could not be written.</summary>
    bool TrySet(string id, bool value, DateTimeOffset at, string actor);
}

/// <summary>The options of a sandbox: in memory, forgotten at a restart.</summary>
public sealed class InMemoryOptionStore : IOptionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, bool> _values = new(StringComparer.Ordinal);

    public bool? Get(string id)
    {
        lock (_gate)
        {
            return _values.TryGetValue(id, out var value) ? value : null;
        }
    }

    public bool TrySet(string id, bool value, DateTimeOffset at, string actor)
    {
        lock (_gate)
        {
            _values[id] = value;
            return true;
        }
    }
}

/// <summary>Why an option was not changed.</summary>
public enum OptionRefusal
{
    UnknownOption,

    /// <summary>The host was not started to allow lowering security: this option cannot be turned off from the screen, whoever asks.</summary>
    LockedByHost,

    /// <summary>The host configuration fixes this option: the screen cannot change it.</summary>
    SetByConfiguration,

    /// <summary>The change could not be traced or saved: nothing was changed.</summary>
    Unavailable,
}

/// <summary>An option as the screen shows it.</summary>
/// <param name="Id">The identifier.</param>
/// <param name="Value">What is in force now.</param>
/// <param name="Default">What it is until someone changes it.</param>
/// <param name="LowersSecurityWhenOff">Turning it off makes the product less safe: it needs the host's leave, an administrator and the password.</param>
/// <param name="CanTurnOff">Whether the host lets it be turned off now (false: locked, or fixed by the configuration).</param>
/// <param name="LockedReason"><c>host</c> or <c>configuration</c> when it cannot be turned off, otherwise null.</param>
public sealed record OptionState(string Id, bool Value, bool Default, bool LowersSecurityWhenOff, bool CanTurnOff, string? LockedReason);
