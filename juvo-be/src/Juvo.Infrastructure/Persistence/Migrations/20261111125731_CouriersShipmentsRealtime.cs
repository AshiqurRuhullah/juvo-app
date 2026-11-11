using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juvo.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CouriersShipmentsRealtime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Couriers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    CodSupported = table.Column<bool>(type: "bit", nullable: false),
                    SupportsInternational = table.Column<bool>(type: "bit", nullable: false),
                    MinWeightGrams = table.Column<int>(type: "int", nullable: true),
                    MaxWeightGrams = table.Column<int>(type: "int", nullable: true),
                    SupportedZones = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ChargeOverride = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    BaseRate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PerExtraKgRate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    BaseRateUpToGrams = table.Column<int>(type: "int", nullable: false),
                    TrackingUrlTemplate = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SettingsJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Couriers", x => x.Id);
                    table.CheckConstraint("CK_Couriers_Rates", "[BaseRate] >= 0 AND [PerExtraKgRate] >= 0 AND [BaseRateUpToGrams] > 0 AND ([ChargeOverride] IS NULL OR [ChargeOverride] >= 0)");
                });

            migrationBuilder.CreateTable(
                name: "SandboxConsignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsignmentId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    CodAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    IsReturn = table.Column<bool>(type: "bit", nullable: false),
                    RawStatus = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SandboxConsignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CodSettlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CourierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CourierName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ExpectedTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ReceivedTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    FeesTotal = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedByName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    ReconciledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReconciledByName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    SettledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SettledByName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodSettlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CodSettlements_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CodSettlements_Couriers_CourierId",
                        column: x => x.CourierId,
                        principalTable: "Couriers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Shipments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CourierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CourierCode = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CourierName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ReturnRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ConsignmentId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    TrackingUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    WeightGrams = table.Column<int>(type: "int", nullable: false),
                    LengthCm = table.Column<int>(type: "int", nullable: true),
                    WidthCm = table.Column<int>(type: "int", nullable: true),
                    HeightCm = table.Column<int>(type: "int", nullable: true),
                    CodAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CodCollectedAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    CodCollectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CodSettlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Cost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PickupName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    PickupPhone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    PickupAddress = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    DeliveryName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    DeliveryPhone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DeliveryAddress = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: false),
                    DeliveryDistrict = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    LastRawStatus = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    BookedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PickedUpAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReturnedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LabelPrintedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastPolledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shipments", x => x.Id);
                    table.CheckConstraint("CK_Shipments_Amounts", "[CodAmount] >= 0 AND [Cost] >= 0 AND [WeightGrams] > 0 AND [Attempts] >= 0");
                    table.ForeignKey(
                        name: "FK_Shipments_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Shipments_Couriers_CourierId",
                        column: x => x.CourierId,
                        principalTable: "Couriers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Shipments_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CodSettlementItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SettlementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ConsignmentId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Expected = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CourierFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Received = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodSettlementItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CodSettlementItems_CodSettlements_SettlementId",
                        column: x => x.SettlementId,
                        principalTable: "CodSettlements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShipmentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderEventId = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    RawStatus = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IsStatusChange = table.Column<bool>(type: "bit", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    ActorName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipmentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShipmentEvents_Shipments_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "Shipments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CodSettlementItems_SettlementId",
                table: "CodSettlementItems",
                column: "SettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_CodSettlementItems_ShipmentId",
                table: "CodSettlementItems",
                column: "ShipmentId");

            migrationBuilder.CreateIndex(
                name: "IX_CodSettlements_BranchId_Status_CreatedAt",
                table: "CodSettlements",
                columns: new[] { "BranchId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CodSettlements_CourierId_Status",
                table: "CodSettlements",
                columns: new[] { "CourierId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CodSettlements_Number",
                table: "CodSettlements",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Couriers_Code",
                table: "Couriers",
                column: "Code",
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Couriers_IsEnabled_Priority",
                table: "Couriers",
                columns: new[] { "IsEnabled", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_SandboxConsignments_ConsignmentId",
                table: "SandboxConsignments",
                column: "ConsignmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentEvents_ShipmentId_OccurredAt",
                table: "ShipmentEvents",
                columns: new[] { "ShipmentId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ShipmentEvents_ShipmentId_ProviderEventId",
                table: "ShipmentEvents",
                columns: new[] { "ShipmentId", "ProviderEventId" },
                unique: true,
                filter: "[ProviderEventId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_BranchId_Status_BookedAt",
                table: "Shipments",
                columns: new[] { "BranchId", "Status", "BookedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_CodSettlementId",
                table: "Shipments",
                column: "CodSettlementId");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_CourierId_ConsignmentId",
                table: "Shipments",
                columns: new[] { "CourierId", "ConsignmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_OrderId_BookedAt",
                table: "Shipments",
                columns: new[] { "OrderId", "BookedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_OrderNumber",
                table: "Shipments",
                column: "OrderNumber");

            migrationBuilder.CreateIndex(
                name: "UX_Shipments_OpenForward",
                table: "Shipments",
                column: "OrderId",
                unique: true,
                filter: "[Kind] = 1 AND [Status] IN (1, 2, 3, 4, 6) AND [IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "UX_Shipments_OpenReturn",
                table: "Shipments",
                column: "ReturnRequestId",
                unique: true,
                filter: "[Kind] = 2 AND [Status] IN (1, 2, 3, 4, 6) AND [IsDeleted] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CodSettlementItems");

            migrationBuilder.DropTable(
                name: "SandboxConsignments");

            migrationBuilder.DropTable(
                name: "ShipmentEvents");

            migrationBuilder.DropTable(
                name: "CodSettlements");

            migrationBuilder.DropTable(
                name: "Shipments");

            migrationBuilder.DropTable(
                name: "Couriers");
        }
    }
}
