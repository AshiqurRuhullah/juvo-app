using Juvo.Domain.Branches;
using Juvo.Domain.Customers;
using Juvo.Domain.Identity;

namespace Juvo.Domain.Tests.Identity;

public class IdentityDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("HO", "HO")]
    [InlineData("dhk-01", "DHK-01")]
    [InlineData(" shop2 ", "SHOP2")]
    public void Branch_code_is_normalized(string input, string expected) => Assert.Equal(expected, Branch.NormalizeCode(input));

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("-HO")]
    [InlineData("HO 1")]
    [InlineData("THIS-CODE-IS-WAY-TOO-LONG")]
    [InlineData("ঢাকা")]
    public void Invalid_branch_codes_are_rejected(string input)
    {
        Assert.False(Branch.IsValidCode(input));
        Assert.Throws<ArgumentException>(() => Branch.NormalizeCode(input));
    }

    [Fact]
    public void New_branch_gets_an_order_prefix_from_its_code_and_can_change_it()
    {
        var branch = new Branch("DHK-01", "Dhaka Shop", "ঢাকা শাখা", BranchType.Shop);

        Assert.Equal("DHK01", branch.OrderPrefix);
        Assert.True(branch.IsActive);
        Assert.Equal("Asia/Dhaka", branch.TimeZoneId);
        Assert.Equal("BDT", branch.DefaultCurrency);

        branch.SetOrderPrefix("dk1");
        Assert.Equal("DK1", branch.OrderPrefix);
        Assert.Throws<ArgumentException>(() => branch.SetOrderPrefix("bad prefix"));
    }

    [Fact]
    public void Deactivating_a_branch_clears_the_default_flag_and_a_branch_cannot_be_its_own_parent()
    {
        var branch = new Branch("HO", "Head Office", null, BranchType.HeadOffice);
        branch.MarkDefault();

        branch.Deactivate();

        Assert.False(branch.IsActive);
        Assert.False(branch.IsDefault);
        Assert.Throws<ArgumentException>(() => branch.UpdateDetails("Head Office", null, BranchType.HeadOffice, branch.Id,
            null, null, null, null, null, null, "Asia/Dhaka", "BDT", null, null, null));
    }

    [Fact]
    public void Refresh_token_is_active_until_used_revoked_or_expired()
    {
        var token = new RefreshToken(SubjectType.Staff, Guid.NewGuid(), Guid.NewGuid(), "hash", Now.AddDays(14), "1.2.3.4", "UA", "Chrome", Now);
        Assert.True(token.IsActive(Now));
        Assert.False(token.IsActive(Now.AddDays(15)));

        token.MarkUsed(Now, Guid.NewGuid());
        Assert.False(token.IsActive(Now));
        Assert.True(token.IsUsed);

        var other = new RefreshToken(SubjectType.Staff, Guid.NewGuid(), Guid.NewGuid(), "h2", Now.AddDays(14), null, null, null, Now);
        other.Revoke(Now, "logout");
        other.Revoke(Now.AddMinutes(1), "second reason is ignored");
        Assert.False(other.IsActive(Now));
        Assert.Equal("logout", other.RevokedReason);
        Assert.Equal(Now, other.RevokedAt);
    }

    [Fact]
    public void Refresh_token_truncates_oversized_client_strings()
    {
        var token = new RefreshToken(SubjectType.Staff, Guid.NewGuid(), Guid.NewGuid(), "h", Now.AddDays(1), null,
            new string('u', 1000), new string('d', 500), Now);

        Assert.Equal(400, token.UserAgent!.Length);
        Assert.Equal(100, token.DeviceName!.Length);
    }

    [Fact]
    public void Otp_is_usable_only_before_expiry_and_below_the_attempt_limit()
    {
        var otp = new OtpCode("+8801712345678", OtpPurpose.SignIn, "hash", Now.AddMinutes(5), "1.1.1.1");
        Assert.True(otp.IsUsable(Now));
        Assert.False(otp.IsUsable(Now.AddMinutes(5)));

        for (var i = 0; i < OtpCode.MaxAttempts; i++)
        {
            otp.RegisterFailedAttempt();
        }

        Assert.False(otp.IsUsable(Now));

        var fresh = new OtpCode("+8801712345678", OtpPurpose.SignIn, "hash", Now.AddMinutes(5), null);
        fresh.Consume(Now);
        Assert.False(fresh.IsUsable(Now));
    }

    [Fact]
    public void Customer_becomes_quick_when_the_mobile_is_verified_and_full_when_named()
    {
        var customer = new Customer("+8801712345678", ProfileLevel.Guest);

        customer.VerifyMobile(Now);
        Assert.Equal(ProfileLevel.Quick, customer.ProfileLevel);
        Assert.True(customer.MobileVerified);

        customer.UpdateProfile("Rahim Uddin", "rahim@example.com");
        Assert.Equal(ProfileLevel.Full, customer.ProfileLevel);
    }

    [Fact]
    public void Staff_user_permission_version_changes_when_access_changes()
    {
        var user = new StaffUser { UserName = "a", Email = "a@juvo.test", FullName = "A" };
        var start = user.PermissionVersion;

        user.Deactivate();
        Assert.False(user.IsActive);
        Assert.Equal(start + 1, user.PermissionVersion);

        user.Activate();
        Assert.True(user.IsActive);
        Assert.Equal(start + 2, user.PermissionVersion);

        user.MarkDeleted(Now, null);
        Assert.True(user.IsDeleted);
        Assert.False(user.IsActive);
        Assert.Equal(7, user.Id.Version);
    }
}
