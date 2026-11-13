namespace Orchestrarr.Core.Missions;

/// <summary>What an application needs in staging (FR-STG-1): nothing, its dependencies, or the whole constellation.</summary>
public enum StagingRequirement
{
    /// <summary>A blank machine is enough.</summary>
    BlankMachine,
    Dependencies,
    FullStack,
}

/// <summary>Where an application's staging is (FR-STG-3).</summary>
public enum StagingState
{
    NotDefined,
    Defined,
    Provisioned,
    BaselineProven,
    Stale,
    Broken,
}

/// <summary>
/// What the maintainer declared about an application's staging. It lives in the description of the perimeter, which only the maintainer edits, never in the application's own
/// repository (a worker could change it). <see cref="Undeclared"/> is not the same as <see cref="DeclaredNoStaging"/>.
/// </summary>
public enum StagingDeclaration
{
    /// <summary>Nobody described it: refused (fail closed, unknown counts as stopped).</summary>
    Undeclared,

    /// <summary>The maintainer declared that the application has a staging, with a requirement.</summary>
    DeclaredStaging,

    /// <summary>The maintainer declared that the application has no staging: an accepted risk (CASE-17).</summary>
    DeclaredNoStaging,
}

public enum StagingVerdict
{
    /// <summary>A mandate on the code may be granted.</summary>
    Usable,

    /// <summary>Granted with no net: declared as having no staging, recorded as an accepted risk.</summary>
    UsableWithNoNet,

    /// <summary>Refused: not declared (fail closed).</summary>
    RefusedUndeclared,

    /// <summary>Refused: the staging is not defined, not provisioned or its baseline is not proven.</summary>
    RefusedNotProven,

    /// <summary>Refused: broken or stale until the baseline is proved again.</summary>
    RefusedBrokenOrStale,
}

/// <summary>The gate of FR-STG-4 and ADR 0034 point 8, as a pure function.</summary>
public static class StagingGate
{
    public static StagingVerdict Decide(StagingDeclaration declaration, StagingRequirement requirement, StagingState state)
    {
        switch (declaration)
        {
            case StagingDeclaration.DeclaredNoStaging:
                return StagingVerdict.UsableWithNoNet;
            case StagingDeclaration.Undeclared:
                return StagingVerdict.RefusedUndeclared;
            default:
                break;
        }
        if (state is StagingState.Broken or StagingState.Stale)
        {
            return StagingVerdict.RefusedBrokenOrStale;
        }
        if (requirement == StagingRequirement.BlankMachine || state == StagingState.BaselineProven)
        {
            return StagingVerdict.Usable;
        }
        return StagingVerdict.RefusedNotProven;
    }
}
