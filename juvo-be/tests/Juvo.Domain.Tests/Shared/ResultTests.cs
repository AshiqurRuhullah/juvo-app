using Juvo.Shared.Results;

namespace Juvo.Domain.Tests.Shared;

public class ResultTests
{
    [Fact]
    public void Success_has_no_error()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_carries_error_code_and_type()
    {
        var result = Result.Failure(Error.NotFound());

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.General.NotFound, result.Error!.Code);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
    }

    [Fact]
    public void Typed_success_exposes_value()
    {
        Result<int> result = 42;

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Typed_failure_throws_when_value_is_read()
    {
        Result<int> result = Error.Conflict();

        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Match_picks_the_matching_branch()
    {
        Result<int> ok = 5;
        Result<int> bad = Error.Forbidden();

        Assert.Equal("ok:5", ok.Match(v => $"ok:{v}", e => e.Code));
        Assert.Equal(ErrorCodes.Auth.Forbidden, bad.Match(v => $"ok:{v}", e => e.Code));
    }

    [Fact]
    public void Validation_error_keeps_field_messages()
    {
        var error = Error.Validation(new Dictionary<string, string[]> { ["name"] = ["Required"] });

        Assert.Equal(ErrorType.Validation, error.Type);
        Assert.Equal("Required", error.ValidationErrors["name"][0]);
    }
}
