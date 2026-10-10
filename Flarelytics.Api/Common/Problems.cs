using System.Diagnostics;
using Flarelytics.Core.Stores;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flarelytics.Api.Common;

/// <summary>
/// Un errore previsto, che diventa una risposta Problem Details con il suo
/// stato e un <c>code</c> stabile.
/// </summary>
/// <remarks>
/// Il <c>code</c> è il contratto con il frontend: il <c>detail</c> è una frase
/// per le persone e può cambiare, il codice no. È quello su cui il frontend
/// decide cosa fare e, quando ci saranno più lingue, cosa mostrare.
/// </remarks>
public class ApiProblem(int status, string code, string detail) : Exception(detail)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public static ApiProblem NotFound(string what) =>
        new(StatusCodes.Status404NotFound, "not_found", $"{what} non trovato.");

    public static ApiProblem Conflict(string code, string detail) =>
        new(StatusCodes.Status409Conflict, code, detail);

    public static ApiProblem Forbidden(string detail) =>
        new(StatusCodes.Status403Forbidden, "forbidden", detail);

    public static ApiProblem BadRequest(string code, string detail) =>
        new(StatusCodes.Status400BadRequest, code, detail);
}

/// <summary>
/// Trasforma in Problem Details le eccezioni che hanno un significato per il
/// client. Tutto il resto è un 500 senza dettagli: lo stack trace va nel log,
/// non in risposta.
/// </summary>
public class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, code, detail) = exception switch
        {
            ApiProblem p => (p.Status, p.Code, p.Message),
            InvalidStoreKeyException e => (StatusCodes.Status400BadRequest, "invalid_store_key", e.Message),
            StoreAccessException e => (StatusCodes.Status502BadGateway, "store_error", e.Message),

            // Storage dei file non configurato (modalità remota imposta) o Bunny
            // che non risponde. Il messaggio non contiene mai la password della zone.
            Flarelytics.Core.Social.Media.MediaStorageUnavailableException e => (StatusCodes.Status503ServiceUnavailable, "media_storage_unavailable", e.Message),

            // Due richieste che creano la stessa cosa nello stesso istante
            // superano entrambe il controllo applicativo: l'indice unico è
            // l'ultima parola, e la risposta giusta è un 409, non un 500.
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                (StatusCodes.Status409Conflict, "conflict", "Esiste già un elemento con questi dati."),

            _ => (StatusCodes.Status500InternalServerError, "internal_error", "Errore interno.")
        };

        if (status >= 500) log.LogError(exception, "Errore non gestito su {Path}", http.Request.Path);

        http.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Detail = detail,
                Extensions =
                {
                    ["code"] = code,
                    ["traceId"] = Activity.Current?.Id ?? http.TraceIdentifier
                }
            }
        });
    }
}
