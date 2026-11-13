namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>How an operation ended, with what its caller needs to tell why it was refused.</summary>
public class SiteBuilderOperationResult
{
    public SiteBuilderOperationStatus Status { get; init; }

    public bool Succeeded => Status == SiteBuilderOperationStatus.Succeeded;

    /// <summary>Why it was refused, for an invalid request, a conflict or a missing version.</summary>
    public string Error { get; init; }

    /// <summary>The problems one by one, when <see cref="Error"/> sums up several; <c>null</c> otherwise.</summary>
    public IReadOnlyList<string> Errors { get; init; }

    /// <summary>What a dry run would change; <c>null</c> for an operation that ran.</summary>
    public SiteBuilderChangePreview Preview { get; init; }

    public static SiteBuilderOperationResult Success() => new() { Status = SiteBuilderOperationStatus.Succeeded };

    public static SiteBuilderOperationResult<T> Success<T>(T value) => new() { Status = SiteBuilderOperationStatus.Succeeded, Value = value };

    public static SiteBuilderOperationResult<T> DryRun<T>(SiteBuilderChangePreview preview) =>
        new() { Status = SiteBuilderOperationStatus.Succeeded, Preview = preview };

    public static SiteBuilderOperationResult Forbidden() => new() { Status = SiteBuilderOperationStatus.Forbidden };

    public static SiteBuilderOperationResult NotFound() => new() { Status = SiteBuilderOperationStatus.NotFound };

    public static SiteBuilderOperationResult Invalid(string error, IReadOnlyList<string> errors = null) =>
        new() { Status = SiteBuilderOperationStatus.Invalid, Error = error, Errors = errors };

    public static SiteBuilderOperationResult Conflict(string error) => new() { Status = SiteBuilderOperationStatus.Conflict, Error = error };

    public static SiteBuilderOperationResult VersionRequired(string error) => new() { Status = SiteBuilderOperationStatus.VersionRequired, Error = error };

    /// <summary>The same outcome, for an operation that answers a <typeparamref name="T"/>.</summary>
    public SiteBuilderOperationResult<T> As<T>() => new() { Status = Status, Error = Error, Errors = Errors, Preview = Preview };
}

public class SiteBuilderOperationResult<T> : SiteBuilderOperationResult
{
    /// <summary>What the operation answers; <c>null</c> for a refusal and for a dry run.</summary>
    public T Value { get; init; }
}
