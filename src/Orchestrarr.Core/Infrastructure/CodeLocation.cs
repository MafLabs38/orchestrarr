namespace Orchestrarr.Core.Infrastructure;

/// <summary>How a version is written where it was found.</summary>
public enum VersionForm
{
    /// <summary>A line <c>image: name:tag</c> of a manifest or a compose file.</summary>
    ImageLine,

    /// <summary>A <c>newTag</c> of a kustomization, which names the image next to it.</summary>
    KustomizeTag,

    /// <summary>A string <c>"name:tag"</c> assigned to <c>name</c> or <c>image</c> in a Terraform file.</summary>
    TerraformImage,
}

/// <summary>
/// Where the version of an image is written in the code that deploys it (ADR 0028): the repository, the file, the line, the form, and what is written there.
/// <c>Managed</c> names the automation that rewrites the line by itself (Flux), so that Orchestrarr does not. <c>Environment</c> is the environment the repository deploys to, when the description says.
/// </summary>
public sealed record CodeLocation(string Repository, string Path, int Line, VersionForm Form, ImageReference Image, string? Managed = null, string? Environment = null);
