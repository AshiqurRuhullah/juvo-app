using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juvo.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GrowthModules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderItems_Quantity",
                table: "OrderItems");

            migrationBuilder.AddColumn<bool>(
                name: "AllowPreorder",
                table: "ProductVariants",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PreorderAdvancePercent",
                table: "ProductVariants",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PreorderExpectedShipDate",
                table: "ProductVariants",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowPreorder",
                table: "Products",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "PreorderAdvancePercent",
                table: "Products",
                type: "decimal(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PreorderExpectedShipDate",
                table: "Products",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreorderState",
                table: "Orders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "PromotionDiscount",
                table: "Orders",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "BackorderedQuantity",
                table: "OrderItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsPreorder",
                table: "OrderItems",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "ListPrice",
                table: "OrderItems",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PreorderAllocatedAt",
                table: "OrderItems",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PreorderExpectedShipDate",
                table: "OrderItems",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PromotionDiscount",
                table: "OrderItems",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "StacksWithPromotions",
                table: "Coupons",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Automations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    DelayMinutes = table.Column<int>(type: "int", nullable: false),
                    WindowDays = table.Column<int>(type: "int", nullable: false),
                    CapDays = table.Column<int>(type: "int", nullable: false),
                    SubjectEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SubjectBn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BodyEn = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BodyBn = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LastRunAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Automations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackInStockSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VariantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Mobile = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NotifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackInStockSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackInStockSubscriptions_ProductVariants_VariantId",
                        column: x => x.VariantId,
                        principalTable: "ProductVariants",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_BackInStockSubscriptions_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CreditLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ReferenceKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OrderNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ReturnRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GiftCardId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditLedgerEntries", x => x.Id);
                    table.CheckConstraint("CK_CreditLedgerEntries_Balance", "[BalanceAfter] >= 0 AND [Amount] <> 0");
                    table.ForeignKey(
                        name: "FK_CreditLedgerEntries_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "bit", nullable: false),
                    BuiltInCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerSegments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GiftCards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CodeHint = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    InitialAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    RecipientMobile = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    RecipientEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    CreatedByName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    RedeemedByCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RedeemedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GiftCards", x => x.Id);
                    table.CheckConstraint("CK_GiftCards_Balance", "[Balance] >= 0 AND [InitialAmount] > 0 AND [Balance] <= [InitialAmount]");
                });

            migrationBuilder.CreateTable(
                name: "LoyaltyAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PointsBalance = table.Column<int>(type: "int", nullable: false),
                    CreditBalance = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CodeFailures = table.Column<int>(type: "int", nullable: false),
                    CodeLockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FirstFailureAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoyaltyAccounts", x => x.Id);
                    table.CheckConstraint("CK_LoyaltyAccounts_Balances", "[PointsBalance] >= 0 AND [CreditBalance] >= 0");
                    table.ForeignKey(
                        name: "FK_LoyaltyAccounts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderItemAdjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LabelEn = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    LabelBn = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Level = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderItemAdjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderItemAdjustments_OrderItems_OrderItemId",
                        column: x => x.OrderItemId,
                        principalTable: "OrderItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderTenders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PointsUsed = table.Column<int>(type: "int", nullable: false),
                    PointValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    PointsAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CreditAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    GiftCardAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    GiftCardId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GiftCardHint = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    PointsReturned = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderTenders", x => x.Id);
                    table.CheckConstraint("CK_OrderTenders_Amounts", "[PointsUsed] >= 0 AND [PointsAmount] >= 0 AND [CreditAmount] >= 0 AND [GiftCardAmount] >= 0 AND [PointsReturned] >= 0 AND [PointsReturned] <= [PointsUsed]");
                });

            migrationBuilder.CreateTable(
                name: "PointsLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Points = table.Column<int>(type: "int", nullable: false),
                    BalanceAfter = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ReferenceKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OrderNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ReturnRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PointsLedgerEntries", x => x.Id);
                    table.CheckConstraint("CK_PointsLedgerEntries_Balance", "[BalanceAfter] >= 0 AND [Points] <> 0");
                    table.ForeignKey(
                        name: "FK_PointsLedgerEntries_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Promotions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    NameBn = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    Type = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    StackMode = table.Column<int>(type: "int", nullable: false),
                    Channels = table.Column<int>(type: "int", nullable: false),
                    MinCartValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    UsageLimit = table.Column<int>(type: "int", nullable: true),
                    UsedCount = table.Column<int>(type: "int", nullable: false),
                    DiscountKind = table.Column<int>(type: "int", nullable: true),
                    DiscountValue = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    MaxDiscount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    BuyQuantity = table.Column<int>(type: "int", nullable: false),
                    GetQuantity = table.Column<int>(type: "int", nullable: false),
                    GetDiscountPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    BundlePrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    ZoneCodes = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    LabelEn = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    LabelBn = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Slug = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TitleEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TitleBn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BodyEn = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    BodyBn = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    HeroImagePath = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    BannerImagePath = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    BannerSlot = table.Column<int>(type: "int", nullable: false),
                    BannerCategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Promotions", x => x.Id);
                    table.CheckConstraint("CK_Promotions_Limits", "[UsedCount] >= 0 AND ([UsageLimit] IS NULL OR [UsageLimit] > 0) AND ([MinCartValue] IS NULL OR [MinCartValue] >= 0)");
                    table.CheckConstraint("CK_Promotions_Window", "[StartsAt] IS NULL OR [EndsAt] IS NULL OR [EndsAt] > [StartsAt]");
                });

            migrationBuilder.CreateTable(
                name: "ReferralCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferralCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReferralCodes_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Referrals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferrerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RefereeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeUsed = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    QualifyingOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReferrerReward = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    RefereeReward = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    HouseholdKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AppliedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Referrals", x => x.Id);
                    table.CheckConstraint("CK_Referrals_NotSelf", "[ReferrerId] <> [RefereeId]");
                    table.ForeignKey(
                        name: "FK_Referrals_Customers_RefereeId",
                        column: x => x.RefereeId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Referrals_Customers_ReferrerId",
                        column: x => x.ReferrerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Campaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    SegmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SubjectEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SubjectBn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BodyEn = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BodyBn = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RecipientsReady = table.Column<bool>(type: "bit", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedByName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Campaigns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Campaigns_CustomerSegments_SegmentId",
                        column: x => x.SegmentId,
                        principalTable: "CustomerSegments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GiftCardEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GiftCardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ReferenceKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OrderNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GiftCardEntries", x => x.Id);
                    table.CheckConstraint("CK_GiftCardEntries_Balance", "[BalanceAfter] >= 0");
                    table.ForeignKey(
                        name: "FK_GiftCardEntries_GiftCards_GiftCardId",
                        column: x => x.GiftCardId,
                        principalTable: "GiftCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PromotionTargets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PromotionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromotionTargets", x => x.Id);
                    table.CheckConstraint("CK_PromotionTargets_Quantity", "[Quantity] >= 1");
                    table.ForeignKey(
                        name: "FK_PromotionTargets_Promotions_PromotionId",
                        column: x => x.PromotionId,
                        principalTable: "Promotions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CampaignRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Contact = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SkipReason = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    NotificationLogId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignRecipients_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CampaignRecipients_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_BackorderedQuantity",
                table: "OrderItems",
                column: "BackorderedQuantity",
                filter: "[BackorderedQuantity] > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderItems_Quantity",
                table: "OrderItems",
                sql: "[Quantity] > 0 AND [UnitPrice] >= 0 AND [BackorderedQuantity] >= 0 AND [PromotionDiscount] >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_Automations_Kind",
                table: "Automations",
                column: "Kind",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackInStockSubscriptions_Mobile",
                table: "BackInStockSubscriptions",
                column: "Mobile");

            migrationBuilder.CreateIndex(
                name: "IX_BackInStockSubscriptions_NotifiedAt_CreatedAt",
                table: "BackInStockSubscriptions",
                columns: new[] { "NotifiedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackInStockSubscriptions_ProductId",
                table: "BackInStockSubscriptions",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_BackInStockSubscriptions_VariantId_Mobile",
                table: "BackInStockSubscriptions",
                columns: new[] { "VariantId", "Mobile" },
                unique: true,
                filter: "[NotifiedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_CampaignId_CustomerId",
                table: "CampaignRecipients",
                columns: new[] { "CampaignId", "CustomerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_CampaignId_Status",
                table: "CampaignRecipients",
                columns: new[] { "CampaignId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_CustomerId",
                table: "CampaignRecipients",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CampaignRecipients_NotificationLogId",
                table: "CampaignRecipients",
                column: "NotificationLogId");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_SegmentId",
                table: "Campaigns",
                column: "SegmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_Status_ScheduledAt",
                table: "Campaigns",
                columns: new[] { "Status", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditLedgerEntries_CustomerId_At",
                table: "CreditLedgerEntries",
                columns: new[] { "CustomerId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditLedgerEntries_CustomerId_ReferenceKey",
                table: "CreditLedgerEntries",
                columns: new[] { "CustomerId", "ReferenceKey" },
                unique: true,
                filter: "[ReferenceKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CreditLedgerEntries_OrderId",
                table: "CreditLedgerEntries",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerSegments_BuiltInCode",
                table: "CustomerSegments",
                column: "BuiltInCode",
                unique: true,
                filter: "[BuiltInCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerSegments_Name",
                table: "CustomerSegments",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GiftCardEntries_GiftCardId_At",
                table: "GiftCardEntries",
                columns: new[] { "GiftCardId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_GiftCardEntries_GiftCardId_ReferenceKey",
                table: "GiftCardEntries",
                columns: new[] { "GiftCardId", "ReferenceKey" },
                unique: true,
                filter: "[ReferenceKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GiftCards_CodeHash",
                table: "GiftCards",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GiftCards_IsActive_CreatedAt",
                table: "GiftCards",
                columns: new[] { "IsActive", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LoyaltyAccounts_CustomerId",
                table: "LoyaltyAccounts",
                column: "CustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderItemAdjustments_OrderId",
                table: "OrderItemAdjustments",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItemAdjustments_OrderItemId",
                table: "OrderItemAdjustments",
                column: "OrderItemId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItemAdjustments_SourceId_SourceType",
                table: "OrderItemAdjustments",
                columns: new[] { "SourceId", "SourceType" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderTenders_GiftCardId",
                table: "OrderTenders",
                column: "GiftCardId",
                filter: "[GiftCardId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OrderTenders_OrderId",
                table: "OrderTenders",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PointsLedgerEntries_CustomerId_At",
                table: "PointsLedgerEntries",
                columns: new[] { "CustomerId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_PointsLedgerEntries_CustomerId_ReferenceKey",
                table: "PointsLedgerEntries",
                columns: new[] { "CustomerId", "ReferenceKey" },
                unique: true,
                filter: "[ReferenceKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PointsLedgerEntries_ExpiresAt_CustomerId",
                table: "PointsLedgerEntries",
                columns: new[] { "ExpiresAt", "CustomerId" },
                filter: "[ExpiresAt] IS NOT NULL AND [Points] > 0");

            migrationBuilder.CreateIndex(
                name: "IX_PointsLedgerEntries_OrderId",
                table: "PointsLedgerEntries",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Promotions_IsEnabled_IsArchived_StartsAt_EndsAt",
                table: "Promotions",
                columns: new[] { "IsEnabled", "IsArchived", "StartsAt", "EndsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Promotions_Slug",
                table: "Promotions",
                column: "Slug",
                unique: true,
                filter: "[Slug] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Promotions_Type_IsArchived",
                table: "Promotions",
                columns: new[] { "Type", "IsArchived" });

            migrationBuilder.CreateIndex(
                name: "IX_PromotionTargets_Kind_TargetId",
                table: "PromotionTargets",
                columns: new[] { "Kind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_PromotionTargets_PromotionId_Role",
                table: "PromotionTargets",
                columns: new[] { "PromotionId", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_ReferralCodes_Code",
                table: "ReferralCodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferralCodes_CustomerId",
                table: "ReferralCodes",
                column: "CustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_HouseholdKey",
                table: "Referrals",
                column: "HouseholdKey",
                filter: "[HouseholdKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_QualifyingOrderId",
                table: "Referrals",
                column: "QualifyingOrderId",
                filter: "[QualifyingOrderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_RefereeId",
                table: "Referrals",
                column: "RefereeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Referrals_ReferrerId_Status",
                table: "Referrals",
                columns: new[] { "ReferrerId", "Status" });
        
            // Order lines placed before the pricing engine have no list price: it was their unit price.
            migrationBuilder.Sql("UPDATE [OrderItems] SET [ListPrice] = [UnitPrice] WHERE [ListPrice] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Automations");

            migrationBuilder.DropTable(
                name: "BackInStockSubscriptions");

            migrationBuilder.DropTable(
                name: "CampaignRecipients");

            migrationBuilder.DropTable(
                name: "CreditLedgerEntries");

            migrationBuilder.DropTable(
                name: "GiftCardEntries");

            migrationBuilder.DropTable(
                name: "LoyaltyAccounts");

            migrationBuilder.DropTable(
                name: "OrderItemAdjustments");

            migrationBuilder.DropTable(
                name: "OrderTenders");

            migrationBuilder.DropTable(
                name: "PointsLedgerEntries");

            migrationBuilder.DropTable(
                name: "PromotionTargets");

            migrationBuilder.DropTable(
                name: "ReferralCodes");

            migrationBuilder.DropTable(
                name: "Referrals");

            migrationBuilder.DropTable(
                name: "Campaigns");

            migrationBuilder.DropTable(
                name: "GiftCards");

            migrationBuilder.DropTable(
                name: "Promotions");

            migrationBuilder.DropTable(
                name: "CustomerSegments");

            migrationBuilder.DropIndex(
                name: "IX_OrderItems_BackorderedQuantity",
                table: "OrderItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderItems_Quantity",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "AllowPreorder",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "PreorderAdvancePercent",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "PreorderExpectedShipDate",
                table: "ProductVariants");

            migrationBuilder.DropColumn(
                name: "AllowPreorder",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PreorderAdvancePercent",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PreorderExpectedShipDate",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "PreorderState",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PromotionDiscount",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "BackorderedQuantity",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "IsPreorder",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "ListPrice",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "PreorderAllocatedAt",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "PreorderExpectedShipDate",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "PromotionDiscount",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "StacksWithPromotions",
                table: "Coupons");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderItems_Quantity",
                table: "OrderItems",
                sql: "[Quantity] > 0 AND [UnitPrice] >= 0");
        }
    }
}
