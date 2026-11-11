namespace Juvo.Shared.Security;

/// <summary>Constants for permission codes used in code. The full list lives in <see cref="PermissionCatalog"/>.</summary>
public static class Permissions
{
    public static class Ums
    {
        public const string UserView = "ums.user.view";
        public const string UserCreate = "ums.user.create";
        public const string UserEdit = "ums.user.edit";
        public const string UserActivate = "ums.user.activate";
        public const string UserDeactivate = "ums.user.deactivate";
        public const string UserResetPassword = "ums.user.reset-password";
        public const string UserForceLogout = "ums.user.force-logout";
        public const string UserManageTwoFactor = "ums.user.manage-two-factor";
        public const string RoleView = "ums.role.view";
        public const string RoleCreate = "ums.role.create";
        public const string RoleEdit = "ums.role.edit";
        public const string RoleDelete = "ums.role.delete";
        public const string RoleClone = "ums.role.clone";
        public const string RoleAssignPermissions = "ums.role.assign-permissions";
        public const string PermissionView = "ums.permission.view";
        public const string BranchView = "ums.branch.view";
        public const string BranchCreate = "ums.branch.create";
        public const string BranchEdit = "ums.branch.edit";
        public const string BranchDeactivate = "ums.branch.deactivate";
        public const string BranchSetDefault = "ums.branch.set-default";
        public const string BranchAssignUser = "ums.branch.assign-user";
        public const string BranchSwitchAll = "ums.branch.switch-all";
        public const string SessionView = "ums.session.view";
        public const string SessionRevoke = "ums.session.revoke";
        public const string SignInView = "ums.sign-in.view";
        public const string AuditView = "ums.audit.view";
    }

    public static class Catalog
    {
        public const string ProductView = "catalog.product.view";
        public const string ProductCreate = "catalog.product.create";
        public const string ProductEdit = "catalog.product.edit";
        public const string ProductPublish = "catalog.product.publish";
        public const string ProductArchive = "catalog.product.archive";
        public const string ProductDuplicate = "catalog.product.duplicate";
        public const string ProductImport = "catalog.product.import";
        public const string ProductExport = "catalog.product.export";
        public const string ProductViewCost = "catalog.product.view-cost";
        public const string CategoryView = "catalog.category.view";
        public const string CategoryCreate = "catalog.category.create";
        public const string CategoryEdit = "catalog.category.edit";
        public const string CategoryDelete = "catalog.category.delete";
        public const string BrandView = "catalog.brand.view";
        public const string BrandCreate = "catalog.brand.create";
        public const string BrandEdit = "catalog.brand.edit";
        public const string BrandDelete = "catalog.brand.delete";
        public const string SizeChartView = "catalog.size-chart.view";
        public const string SizeChartEdit = "catalog.size-chart.edit";
        public const string MediaUpload = "catalog.media.upload";
        public const string MediaDelete = "catalog.media.delete";
    }

    public static class Inventory
    {
        public const string StockView = "inventory.stock.view";
        public const string StockAdjust = "inventory.stock.adjust";
        public const string TransferView = "inventory.transfer.view";
        public const string TransferRequest = "inventory.transfer.request";
        public const string TransferApprove = "inventory.transfer.approve";
        public const string TransferReceive = "inventory.transfer.receive";
        public const string TransferCancel = "inventory.transfer.cancel";
        public const string CountView = "inventory.count.view";
        public const string CountCreate = "inventory.count.create";
        public const string CountApprove = "inventory.count.approve";
        public const string ReorderView = "inventory.reorder.view";
        public const string ReorderEdit = "inventory.reorder.edit";
    }

    public static class Customers
    {
        public const string CustomerView = "customers.customer.view";
        public const string CustomerEdit = "customers.customer.edit";
        public const string CustomerBlock = "customers.customer.block";
        public const string CustomerExport = "customers.customer.export";
        public const string CustomerViewPii = "customers.customer.view-pii";
    }

    public static class Delivery
    {
        public const string ZoneView = "delivery.zone.view";
        public const string ZoneCreate = "delivery.zone.create";
        public const string ZoneEdit = "delivery.zone.edit";
        public const string ZoneDelete = "delivery.zone.delete";
        public const string RateView = "delivery.rate.view";
        public const string RateEdit = "delivery.rate.edit";
        public const string CountryView = "delivery.country.view";
        public const string CountryEnable = "delivery.country.enable";
    }

    public static class Promotions
    {
        public const string CouponView = "promotions.coupon.view";
        public const string CouponCreate = "promotions.coupon.create";
        public const string CouponEdit = "promotions.coupon.edit";
        public const string CouponDelete = "promotions.coupon.delete";
    }
}
