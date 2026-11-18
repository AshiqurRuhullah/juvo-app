using System.Text.Json;
using System.Text.Json.Serialization;
using Juvo.Application.Abstractions;
using Juvo.Application.Customers;
using Juvo.Application.Ums;
using Juvo.Domain.Customers;
using Juvo.Domain.Marketing;
using Juvo.Domain.Orders;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Marketing;

internal static class SegmentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string Write(SegmentDefinition definition) => JsonSerializer.Serialize(definition, Options);

    public static SegmentDefinition Read(string json) => JsonSerializer.Deserialize<SegmentDefinition>(json, Options) ?? new SegmentDefinition();
}

/// <summary>The segments that ship with the platform (FR-AC01). Staff copy one to change it.</summary>
public static class BuiltInSegments
{
    public static readonly IReadOnlyList<(string Code, string Name, string Description, SegmentDefinition Definition)> All =
    [
        ("new-customers", "New customers (30 days)", "Customers who joined in the last 30 days.", One(new SegmentRule { Type = SegmentRuleTypes.NewCustomers, Days = 30 })),
        ("repeat-buyers", "Repeat buyers", "Customers with at least 2 orders (cancelled and returned orders do not count).", One(new SegmentRule { Type = SegmentRuleTypes.RepeatBuyers, MinOrders = 2 })),
        ("high-spenders", "High spenders", "Customers who spent BDT 10,000 or more in total.", One(new SegmentRule { Type = SegmentRuleTypes.HighSpenders, MinSpend = 10000m })),
        ("inactive-60", "Inactive for 60 days", "Customers with no order and no sign-in in the last 60 days.", One(new SegmentRule { Type = SegmentRuleTypes.Inactive, Days = 60 })),
    ];

    private static SegmentDefinition One(SegmentRule rule) => new() { Combinator = "and", Rules = [rule] };
}

/// <summary>Saved segments, live counts and member lists (module AC, FR-AC01).</summary>
public sealed class SegmentsService(IApplicationDbContext db, SegmentQueryBuilder builder, ActorContext actors)
{
    private static Error NotFound() => Error.NotFound(ErrorCodes.Marketing.SegmentNotFound, "Segment not found.");

    private static Error Invalid(Dictionary<string, string[]> errors) => new(ErrorCodes.Marketing.SegmentInvalid, "The segment is not valid.", ErrorType.Validation, errors);

    private static SegmentDto ToDto(CustomerSegment s) => new(s.Id, s.Name, s.Description, s.IsBuiltIn, s.BuiltInCode, SegmentJson.Read(s.DefinitionJson), s.CreatedAt, s.UpdatedAt);

    /// <summary>Adds the built-in segments that are missing. Safe to call often and from two places at once.</summary>
    public async Task EnsureBuiltInsAsync(CancellationToken cancellationToken)
    {
        var present = await db.CustomerSegments.AsNoTracking().Where(s => s.BuiltInCode != null).Select(s => s.BuiltInCode!).ToListAsync(cancellationToken);
        var missing = BuiltInSegments.All.Where(b => !present.Contains(b.Code)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        foreach (var (code, name, description, definition) in missing)
        {
            db.CustomerSegments.Add(new CustomerSegment(name, description, SegmentJson.Write(definition), true, code));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.DiscardUnsavedAdditions(); // another request seeded them first
        }
    }

    public async Task<Result<PagedResult<SegmentDto>>> ListAsync(PageRequest request, CancellationToken cancellationToken)
    {
        await EnsureBuiltInsAsync(cancellationToken);
        var query = db.CustomerSegments.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(s => s.Name.Contains(term));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(s => s.IsBuiltIn).ThenBy(s => s.Name).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<SegmentDto>([.. rows.Select(ToDto)], total, page.Page, page.PageSize);
    }

    public async Task<Result<SegmentDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await EnsureBuiltInsAsync(cancellationToken);
        var segment = await db.CustomerSegments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        return segment is null ? NotFound() : ToDto(segment);
    }

    private async Task<Error?> ValidateAsync(Guid? id, SaveSegmentRequest r, CancellationToken cancellationToken)
    {
        var errors = SegmentValidator.Validate(r.Definition) ?? [];
        var name = r.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 120)
        {
            errors["name"] = ["Give the segment a name (up to 120 characters)."];
        }
        else if (await db.CustomerSegments.AnyAsync(s => s.Name == name && s.Id != id, cancellationToken))
        {
            errors["name"] = ["A segment with this name already exists."];
        }

        if (r.Description is { Length: > 400 })
        {
            errors["description"] = ["The description can have 400 characters."];
        }

        return errors.Count == 0 ? null : Invalid(errors);
    }

    public async Task<Result<SegmentDto>> CreateAsync(SaveSegmentRequest r, CancellationToken cancellationToken)
    {
        if (await ValidateAsync(null, r, cancellationToken) is { } error)
        {
            return error;
        }

        var segment = new CustomerSegment(r.Name!, r.Description, SegmentJson.Write(r.Definition!), false, null);
        db.CustomerSegments.Add(segment);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(segment);
    }

    public async Task<Result<SegmentDto>> UpdateAsync(Guid id, SaveSegmentRequest r, CancellationToken cancellationToken)
    {
        var segment = await db.CustomerSegments.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (segment is null)
        {
            return NotFound();
        }

        if (segment.IsBuiltIn)
        {
            return Error.Conflict(ErrorCodes.Marketing.SegmentBuiltIn, "A built-in segment cannot be changed. Copy it first.");
        }

        if (await ValidateAsync(id, r, cancellationToken) is { } error)
        {
            return error;
        }

        segment.Update(r.Name!, r.Description, SegmentJson.Write(r.Definition!));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(segment);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var segment = await db.CustomerSegments.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (segment is null)
        {
            return NotFound();
        }

        if (segment.IsBuiltIn)
        {
            return Error.Conflict(ErrorCodes.Marketing.SegmentBuiltIn, "A built-in segment cannot be deleted.");
        }

        if (await db.Campaigns.AnyAsync(c => c.SegmentId == id, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Marketing.SegmentInUse, "A campaign uses this segment, so it cannot be deleted.");
        }

        db.CustomerSegments.Remove(segment);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<SegmentDto>> CopyAsync(Guid id, CancellationToken cancellationToken)
    {
        var source = await db.CustomerSegments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null)
        {
            return NotFound();
        }

        var baseName = source.Name.Length > 100 ? source.Name[..100] : source.Name;
        var name = $"{baseName} (copy)";
        for (var n = 2; await db.CustomerSegments.AnyAsync(s => s.Name == name, cancellationToken); n++)
        {
            name = $"{baseName} (copy {n})";
        }

        var copy = new CustomerSegment(name, source.Description, source.DefinitionJson, false, null);
        db.CustomerSegments.Add(copy);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(copy);
    }

    private async Task<Result<SegmentDefinition>> DefinitionAsync(Guid id, CancellationToken cancellationToken)
    {
        var json = await db.CustomerSegments.AsNoTracking().Where(s => s.Id == id).Select(s => s.DefinitionJson).FirstOrDefaultAsync(cancellationToken);
        return json is null ? NotFound() : SegmentJson.Read(json);
    }

    /// <summary>How many active customers match, and how many of them can be reached by each channel (consented and contactable).</summary>
    public async Task<SegmentCountDto> CountAsync(SegmentDefinition definition, CancellationToken cancellationToken)
    {
        var members = builder.Members(definition);
        var total = await members.CountAsync(cancellationToken);
        var sms = await members.Where(builder.SmsReachable()).CountAsync(cancellationToken);
        var email = await members.Where(builder.EmailReachable()).CountAsync(cancellationToken);
        return new SegmentCountDto(total, new ReachableDto(sms, email));
    }

    public async Task<Result<SegmentCountDto>> CountAsync(Guid id, CancellationToken cancellationToken)
    {
        var definition = await DefinitionAsync(id, cancellationToken);
        return definition.IsFailure ? definition.Error! : await CountAsync(definition.Value, cancellationToken);
    }

    public async Task<Result<SegmentCountDto>> PreviewCountAsync(SegmentPreviewRequest r, CancellationToken cancellationToken)
    {
        if (SegmentValidator.Validate(r.Definition) is { } errors)
        {
            return Invalid(errors);
        }

        return await CountAsync(r.Definition!, cancellationToken);
    }

    public async Task<Result<PagedResult<SegmentMemberDto>>> MembersAsync(Guid id, PageRequest request, CancellationToken cancellationToken)
    {
        var definition = await DefinitionAsync(id, cancellationToken);
        if (definition.IsFailure)
        {
            return definition.Error!;
        }

        var actor = await actors.GetAsync(cancellationToken);
        var masked = actor is null || !actor.Access.HasAnywhere(Permissions.Customers.CustomerViewPii);
        var members = builder.Members(definition.Value);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            members = masked
                ? members.Where(c => c.FullName != null && c.FullName.Contains(term))
                : members.Where(c => c.Mobile.Contains(term) || (c.Email != null && c.Email.Contains(term)) || (c.FullName != null && c.FullName.Contains(term)));
        }

        var orders = db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking();
        var rows = members.Select(c => new
        {
            c.Id,
            c.FullName,
            c.Mobile,
            c.Email,
            LastOrderAt = orders.Where(o => o.CustomerId == c.Id).Max(o => (DateTimeOffset?)o.PlacedAt),
            OrderCount = orders.Count(o => o.CustomerId == c.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned),
            Spend = orders.Where(o => o.CustomerId == c.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned).Sum(o => (decimal?)o.GrandTotal) ?? 0m,
            c.CreatedAt,
        });
        var ordered = request.Sort switch
        {
            "spend" => rows.OrderBy(r => r.Spend).ThenBy(r => r.Id),
            "-spend" => rows.OrderByDescending(r => r.Spend).ThenBy(r => r.Id),
            "lastOrder" => rows.OrderBy(r => r.LastOrderAt).ThenBy(r => r.Id),
            "-lastOrder" => rows.OrderByDescending(r => r.LastOrderAt).ThenBy(r => r.Id),
            "-createdAt" => rows.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id),
            _ => rows.OrderBy(r => r.FullName).ThenBy(r => r.Mobile).ThenBy(r => r.Id),
        };

        var page = request.Normalized;
        var total = await members.CountAsync(cancellationToken);
        var pageRows = await ordered.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var ids = pageRows.Select(r => r.Id).ToList();
        var smsOk = (await db.Customers.AsNoTracking().Where(c => ids.Contains(c.Id)).Where(builder.SmsReachable()).Select(c => c.Id).ToListAsync(cancellationToken)).ToHashSet();
        var emailOk = (await db.Customers.AsNoTracking().Where(c => ids.Contains(c.Id)).Where(builder.EmailReachable()).Select(c => c.Id).ToListAsync(cancellationToken)).ToHashSet();
        var items = pageRows.Select(r => new SegmentMemberDto(
            r.Id, r.FullName, masked ? CustomersAdminService.MaskMobile(r.Mobile) : r.Mobile, masked ? CustomersAdminService.MaskEmail(r.Email) : r.Email, r.LastOrderAt, r.OrderCount, r.Spend,
            smsOk.Contains(r.Id), emailOk.Contains(r.Id))).ToList();
        return new PagedResult<SegmentMemberDto>(items, total, page.Page, page.PageSize);
    }
}
