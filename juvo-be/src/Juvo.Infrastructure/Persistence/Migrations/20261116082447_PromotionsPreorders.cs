using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juvo.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PromotionsPreorders : Migration
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

            // Orders placed before module Z had no list price: it was the price they were sold at.
            migrationBuilder.Sql("UPDATE [OrderItems] SET [ListPrice] = [UnitPrice] WHERE [ListPrice] = 0");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderItemAdjustments");

            migrationBuilder.DropTable(
                name: "PromotionTargets");

            migrationBuilder.DropTable(
                name: "Promotions");

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
