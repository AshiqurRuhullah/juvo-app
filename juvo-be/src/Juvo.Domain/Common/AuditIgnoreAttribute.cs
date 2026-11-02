namespace Juvo.Domain.Common;

/// <summary>Excludes a property (password hashes, tokens, secrets) from the audit log's old/new JSON.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditIgnoreAttribute : Attribute
{
}
