using Juvo.Application.Identity;
using Juvo.Domain.Branches;
using Juvo.Domain.Platform;

namespace Juvo.Application.Ums;

// ---- Users ----------------------------------------------------------------------------------------------

public sealed record UserListItemDto(
    Guid Id, string Email, string UserName, string FullName, string? Phone, bool IsActive, bool IsGlobalScope,
    bool TwoFactorEnabled, bool IsLockedOut, DateTimeOffset? LastLoginAt, IReadOnlyList<string> Roles);

public sealed record UserDetailDto(
    Guid Id, string Email, string UserName, string FullName, string? Phone, bool IsActive, bool IsGlobalScope,
    bool TwoFactorEnabled, bool IsLockedOut, bool MustChangePassword, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt,
    IReadOnlyList<RoleAssignmentDto> Assignments);

public sealed record AssignmentRequest(Guid? BranchId, Guid RoleId);

public sealed record CreateUserRequest(
    string Email, string? UserName, string FullName, string? Phone, string? Password, bool IsGlobalScope,
    IReadOnlyList<AssignmentRequest>? Assignments);

public sealed record UpdateUserRequest(string FullName, string? Phone, bool IsGlobalScope);

/// <summary>The temporary password is shown once. The user must change it at the first sign-in.</summary>
public sealed record CreatedUserResponse(UserDetailDto User, string? TemporaryPassword);

public sealed record TemporaryPasswordResponse(string TemporaryPassword);

public sealed record SignInHistoryDto(
    Guid Id, string SubjectType, Guid? SubjectId, string Identifier, bool Succeeded, string? FailureReason,
    string? IpAddress, string? UserAgent, DateTimeOffset OccurredAt);

public sealed record EffectivePermissionsDto(Guid? BranchId, string? BranchCode, bool HasAllPermissions, IReadOnlyList<string> Permissions);

public sealed record UserListQuery(bool? IsActive, Guid? RoleId, Guid? BranchId);

// ---- Roles and permissions ------------------------------------------------------------------------------

public sealed record RoleListItemDto(Guid Id, string Name, string? Description, bool IsSystem, bool IsSuperAdmin, int UserCount, int PermissionCount);

public sealed record RoleDetailDto(Guid Id, string Name, string? Description, bool IsSystem, bool IsSuperAdmin, int UserCount,
    IReadOnlyList<string> Permissions);

public sealed record CreateRoleRequest(string Name, string? Description, IReadOnlyList<string>? Permissions);

public sealed record UpdateRoleRequest(string Name, string? Description);

public sealed record CloneRoleRequest(string Name);

public sealed record SetRolePermissionsRequest(IReadOnlyList<string> Permissions);

public sealed record PermissionItemDto(string Code, string Resource, string Action, string Description);

public sealed record PermissionModuleDto(string Module, IReadOnlyList<PermissionItemDto> Permissions);

public sealed record PermissionCatalogDto(string Version, int Total, IReadOnlyList<PermissionModuleDto> Modules);

// ---- Branches -------------------------------------------------------------------------------------------

public sealed record BranchDto(
    Guid Id, string Code, string NameEn, string? NameBn, BranchType Type, Guid? ParentBranchId, string? AddressLine,
    double? Latitude, double? Longitude, string? Phone, string? Email, string? WorkingHours, string TimeZoneId,
    string DefaultCurrency, string OrderPrefix, string? PickupAddress, string? CourierStoreIds, Guid? DefaultDeliveryZoneId,
    bool IsActive, bool IsDefault, int UserCount);

public sealed record CreateBranchRequest(
    string Code, string NameEn, string? NameBn, BranchType Type, Guid? ParentBranchId, string? AddressLine, double? Latitude,
    double? Longitude, string? Phone, string? Email, string? WorkingHours, string? TimeZoneId, string? DefaultCurrency,
    string? OrderPrefix, string? PickupAddress, string? CourierStoreIds, Guid? DefaultDeliveryZoneId);

public sealed record UpdateBranchRequest(
    string NameEn, string? NameBn, BranchType Type, Guid? ParentBranchId, string? AddressLine, double? Latitude,
    double? Longitude, string? Phone, string? Email, string? WorkingHours, string? TimeZoneId, string? DefaultCurrency,
    string? OrderPrefix, string? PickupAddress, string? CourierStoreIds, Guid? DefaultDeliveryZoneId);

public sealed record BranchUserDto(Guid AssignmentId, Guid UserId, string FullName, string Email, bool IsActive, Guid RoleId, string RoleName);

public sealed record BranchListQuery(bool? IsActive);

// ---- Audit ----------------------------------------------------------------------------------------------

public sealed record AuditLogDto(
    Guid Id, Guid? UserId, string? UserName, Guid? BranchId, string Entity, string EntityId, AuditAction Action,
    string? OldValue, string? NewValue, string? CorrelationId, string? IpAddress, DateTimeOffset OccurredAt);

public sealed record AuditQuery(string? Entity, string? EntityId, Guid? UserId, Guid? BranchId, AuditAction? Action,
    DateTimeOffset? From, DateTimeOffset? To);

public sealed record SignInQuery(Guid? SubjectId, bool? Succeeded, string? SubjectType, DateTimeOffset? From, DateTimeOffset? To);
