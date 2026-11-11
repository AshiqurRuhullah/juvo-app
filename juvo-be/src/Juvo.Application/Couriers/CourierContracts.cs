using Juvo.Domain.Couriers;

namespace Juvo.Application.Couriers;

// ---- Couriers (module H) ------------------------------------------------------------------------------------

public sealed record CourierDto(
    Guid Id, string Code, string Name, string Provider, string ProviderName, bool ProviderConfigured, bool IsEnabled, int Priority, bool CodSupported, bool SupportsInternational,
    int? MinWeightGrams, int? MaxWeightGrams, IReadOnlyList<string> SupportedZoneCodes, decimal? ChargeOverride, decimal BaseRate, decimal PerExtraKgRate, int BaseRateUpToGrams,
    string? TrackingUrlTemplate, string? Notes, IReadOnlyDictionary<string, string> Settings, bool IsSimulator, int OpenShipments, DateTimeOffset CreatedAt);

public sealed record CourierProviderDto(string Code, string Name, bool IsConfigured, bool IsSimulator);

public sealed record SaveCourierRequest(
    string? Code, string Name, string Provider, bool IsEnabled, int Priority, bool CodSupported, bool SupportsInternational, int? MinWeightGrams, int? MaxWeightGrams,
    IReadOnlyList<string>? SupportedZoneCodes, decimal? ChargeOverride, decimal BaseRate, decimal PerExtraKgRate, int? BaseRateUpToGrams, string? TrackingUrlTemplate, string? Notes,
    IReadOnlyDictionary<string, string>? Settings);

public sealed record SetCourierEnabledRequest(bool IsEnabled);

// ---- Shipments ----------------------------------------------------------------------------------------------

public sealed record CourierSuggestionDto(
    Guid CourierId, string Code, string Name, string Provider, bool Eligible, string? Reason, decimal? Cost, int? DaysMin, int? DaysMax, bool Recommended);

public sealed record BookShipmentRequest(Guid? CourierId, int? WeightGrams, int? LengthCm, int? WidthCm, int? HeightCm, string? Note);

public sealed record BulkBookRequest(IReadOnlyList<Guid> OrderIds, Guid? CourierId);

public sealed record BulkBookItemResult(Guid OrderId, string? OrderNumber, bool Ok, Guid? ShipmentId, string? ConsignmentId, string? ErrorCode, string? Message);

public sealed record BulkBookResult(int Succeeded, int Failed, IReadOnlyList<BulkBookItemResult> Results);

public sealed record CancelShipmentRequest(string? Reason);

public sealed record RebookShipmentRequest(Guid? CourierId, int? WeightGrams, int? LengthCm, int? WidthCm, int? HeightCm, string? Note, string? Reason);

/// <summary><c>Status</c> is a raw sandbox status (<c>picked_up</c>, <c>in_transit</c>, <c>out_for_delivery</c>, <c>delivered</c>, <c>delivery_failed</c>, <c>returned</c>, <c>at_hub</c>) or empty for the next step of a normal journey.</summary>
public sealed record AdvanceShipmentRequest(string? Status, string? Note);

public sealed record ShipmentListQuery(ShipmentStatus? Status, Guid? CourierId, Guid? BranchId, ShipmentKind? Kind, Guid? OrderId, bool? CodOnly, bool? Open, DateTimeOffset? From, DateTimeOffset? To);

public sealed record ShipmentListItemDto(
    Guid Id, Guid OrderId, string OrderNumber, Guid BranchId, string CourierCode, string CourierName, ShipmentKind Kind, ShipmentStatus Status, string ConsignmentId, string? TrackingUrl,
    string DeliveryName, string? DeliveryDistrict, int WeightGrams, decimal CodAmount, decimal? CodCollectedAmount, int Attempts, string? FailureReason, DateTimeOffset BookedAt, DateTimeOffset? DeliveredAt);

public sealed record ShipmentEventDto(Guid Id, string? ProviderEventId, string RawStatus, ShipmentStatus Status, bool IsStatusChange, string? Description, string? Location, DateTimeOffset OccurredAt, ShipmentEventSource Source, string? ActorName);

public sealed record ShipmentDto(
    Guid Id, Guid OrderId, string OrderNumber, Guid BranchId, Guid CourierId, string CourierCode, string CourierName, ShipmentKind Kind, Guid? ReturnRequestId, ShipmentStatus Status, string ConsignmentId,
    string? ProviderReference, string? TrackingUrl, int WeightGrams, int? LengthCm, int? WidthCm, int? HeightCm, decimal CodAmount, decimal? CodCollectedAmount, DateTimeOffset? CodCollectedAt,
    Guid? CodSettlementId, decimal Cost, string PickupName, string? PickupPhone, string PickupAddress, string DeliveryName, string DeliveryPhone, string DeliveryAddress, string? DeliveryDistrict,
    string? Note, int Attempts, string? FailureReason, string? LastRawStatus, DateTimeOffset BookedAt, DateTimeOffset? PickedUpAt, DateTimeOffset? DeliveredAt, DateTimeOffset? ReturnedAt,
    DateTimeOffset? CancelledAt, string? CancelReason, bool CanCancel, bool CanSimulate, IReadOnlyList<ShipmentStatus> NextStatuses, IReadOnlyList<ShipmentEventDto> Events);

public sealed record ShipmentLabelDto(
    Guid ShipmentId, string ConsignmentId, string Barcode, string CourierName, string OrderNumber, string PickupName, string? PickupPhone, string PickupAddress, string DeliveryName, string DeliveryPhone,
    string DeliveryAddress, string? DeliveryDistrict, int WeightGrams, decimal CodAmount, int Items, string? Note, string? TrackingUrl, string? ProviderUrl, DateTimeOffset BookedAt, bool IsReturn);

public sealed record CourierWebhookResultDto(string Status, string? ShipmentStatus);

// ---- COD settlement -----------------------------------------------------------------------------------------

public sealed record CreateSettlementRequest(Guid CourierId, Guid? BranchId, IReadOnlyList<Guid>? ShipmentIds, string? Note);

public sealed record ReceivedLineRequest(Guid ShipmentId, decimal Received, string? Note);

/// <summary>Lines left out are taken as received in full.</summary>
public sealed record ReconcileSettlementRequest(IReadOnlyList<ReceivedLineRequest>? Lines);

public sealed record SettleSettlementRequest(string? Reference);

public sealed record SettlementQuery(SettlementStatus? Status, Guid? CourierId, Guid? BranchId);

public sealed record SettlementListItemDto(
    Guid Id, string Number, Guid BranchId, Guid CourierId, string CourierName, SettlementStatus Status, int Parcels, decimal ExpectedTotal, decimal? ReceivedTotal, decimal? Difference, decimal FeesTotal,
    DateTimeOffset CreatedAt, DateTimeOffset? SettledAt);

public sealed record SettlementItemDto(Guid ShipmentId, Guid OrderId, string OrderNumber, string ConsignmentId, decimal Expected, decimal? Received, decimal? Difference, decimal CourierFee, string? Note);

public sealed record SettlementDto(
    Guid Id, string Number, Guid BranchId, Guid CourierId, string CourierName, SettlementStatus Status, decimal ExpectedTotal, decimal? ReceivedTotal, decimal? Difference, decimal FeesTotal,
    string? Reference, string? Note, string? CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset? ReconciledAt, string? ReconciledByName, DateTimeOffset? SettledAt, string? SettledByName,
    DateTimeOffset? CancelledAt, int ShortParcels, int OverParcels, IReadOnlyList<SettlementItemDto> Items);

// ---- Public tracking ----------------------------------------------------------------------------------------

/// <summary>What the customer sees of a parcel: the courier's name, the consignment and the courier's tracking page, plus the scans (no staff names, no internal reasons).</summary>
public sealed record TrackingShipmentDto(string CourierName, string ConsignmentId, string? TrackingUrl, ShipmentStatus Status, int Attempts, IReadOnlyList<TrackingShipmentEventDto> Events);

public sealed record TrackingShipmentEventDto(ShipmentStatus Status, string? Description, string? Location, DateTimeOffset OccurredAt);

