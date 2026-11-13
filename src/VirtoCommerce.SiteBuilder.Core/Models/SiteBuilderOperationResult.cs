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

    /// <summary>
    /// The write reaches what the storefront serves: it publishes or unpublishes a page, or changes the settings, the language
    /// links or the content of a published one. Told on a dry run too, so that a caller can ask for an approval first.
    /// </summary>
    public bool AffectsPublished { get; init; }

    public static SiteBuilderOperationResult Success(bool affectsPublished = false) =>
        new() { Status = SiteBuilderOperationStatus.Succeeded, AffectsPublished = affectsPublished };

    public static SiteBuilderOperationResult<T> Success<T>(T value, bool affectsPublished = false) =>
        new() { Status = SiteBuilderOperationStatus.Succeeded, Value = value, AffectsPublished = affectsPublished };

    public static SiteBuilderOperationResult DryRun(SiteBuilderChangePreview preview) =>
        new() { Status = SiteBuilderOperationStatus.Succeeded, Preview = preview, AffectsPublished = preview?.AffectsPublished == true };

    public static SiteBuilderOperationResult<T> DryRun<T>(SiteBuilderChangePreview preview) =>
        new() { Status = SiteBuilderOperationStatus.Succeeded, Preview = preview, AffectsPublished = preview?.AffectsPublished == true };

    public static SiteBuilderOperationResult Forbidden() => new() { Status = SiteBuilderOperationStatus.Forbidden };

    public static SiteBuilderOperationResult NotFound() => new() { Status = SiteBuilderOperationStatus.NotFound };

    /// <summary>A not-found that tells what was looked for, in <see cref="SiteBuilderOperationResult{T}.Value"/>.</summary>
    public static SiteBuilderOperationResult<T> NotFound<T>(T value) => new() { Status = SiteBuilderOperationStatus.NotFound, Value = value };

    public static SiteBuilderOperationResult Invalid(string error, IReadOnlyList<string> errors = null) =>
        new() { Status = SiteBuilderOperationStatus.Invalid, Error = error, Errors = errors };

    /// <summary>An invalid request that tells what was refused, in <see cref="SiteBuilderOperationResult{T}.Value"/>.</summary>
    public static SiteBuilderOperationResult<T> Invalid<T>(T value, string error, IReadOnlyList<string> errors = null) =>
        new() { Status = SiteBuilderOperationStatus.Invalid, Value = value, Error = error, Errors = errors };

    public static SiteBuilderOperationResult Conflict(string error) => new() { Status = SiteBuilderOperationStatus.Conflict, Error = error };

    public static SiteBuilderOperationResult VersionRequired(string error) => new() { Status = SiteBuilderOperationStatus.VersionRequired, Error = error };

    public static SiteBuilderOperationResult ConcurrentChange(string error) => new() { Status = SiteBuilderOperationStatus.ConcurrentChange, Error = error };

    /// <summary>The same outcome, for an operation that answers a <typeparamref name="T"/>.</summary>
    public SiteBuilderOperationResult<T> As<T>() => new() { Status = Status, Error = Error, Errors = Errors, Preview = Preview, AffectsPublished = AffectsPublished };
}

public class SiteBuilderOperationResult<T> : SiteBuilderOperationResult
{
    /// <summary>What the operation answers; <c>null</c> for a dry run and for a refusal, but a not-found or an invalid request that tells what it refused.</summary>
    public T Value { get; init; }
}
