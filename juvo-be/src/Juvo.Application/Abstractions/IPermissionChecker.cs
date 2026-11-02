namespace Juvo.Application.Abstractions;

/// <summary>Marks a request that needs a permission code such as <c>orders.order.confirm</c>.</summary>
public interface IRequirePermission
{
    string Permission { get; }
}

/// <summary>
/// Answers "may the current user do this, in the active branch?". The Identity module implements it in Step 3.
/// Until then the default implementation denies everything that requires a permission (secure by default).
/// </summary>
public interface IPermissionChecker
{
    Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default);
}
