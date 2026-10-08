using FluentValidation;

namespace Flarelytics.Api.Common;

/// <summary>
/// Esegue il validatore della richiesta prima dell'handler.
/// </summary>
/// <remarks>
/// Chiude anche il caso del campo assente nel JSON, che arriva null nonostante
/// il tipo non nullable e farebbe esplodere l'handler con un 500 invece di un
/// 400.
/// </remarks>
public class ValidationFilter<TRequest>(IValidator<TRequest> validator) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();

        if (request is null)
        {
            return TypedResults.Problem(
                detail: "Il corpo della richiesta è assente o non è JSON valido.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_body" });
        }

        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);
        if (result.IsValid) return await next(context);

        return TypedResults.ValidationProblem(
            result.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()),
            extensions: new Dictionary<string, object?> { ["code"] = "validation_failed" });
    }
}

public static class ValidationFilterExtensions
{
    /// <summary>
    /// Collega la validazione a un endpoint. Se il validatore non è registrato
    /// la rotta fallisce alla prima chiamata: meglio di una validazione
    /// silenziosamente assente.
    /// </summary>
    public static RouteHandlerBuilder Validating<TRequest>(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<ValidationFilter<TRequest>>();
}
