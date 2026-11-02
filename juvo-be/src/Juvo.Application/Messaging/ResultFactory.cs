using System.Reflection;
using Juvo.Shared.Results;

namespace Juvo.Application.Messaging;

/// <summary>Builds a failed <typeparamref name="TResponse"/> (a <see cref="Result"/> or <see cref="Result{T}"/>) from an error.</summary>
internal static class ResultFactory<TResponse>
    where TResponse : Result
{
    private static readonly Func<Error, TResponse> Factory = Build();

    public static TResponse Failure(Error error) => Factory(error);

    private static Func<Error, TResponse> Build()
    {
        if (typeof(TResponse) == typeof(Result))
        {
            return error => (TResponse)(object)Result.Failure(error);
        }

        var valueType = typeof(TResponse).GetGenericArguments()[0];
        var method = typeof(Result).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(Result.Failure) && m.IsGenericMethodDefinition)
            .MakeGenericMethod(valueType);
        return error => (TResponse)method.Invoke(null, [error])!;
    }
}
