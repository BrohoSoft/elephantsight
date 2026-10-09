namespace Flarelytics.Api.Common;

public static partial class Frontend
{
    /// <summary>
    /// Gli header di sicurezza, su ogni risposta. Prima li metteva Caddy; ora
    /// che l'app può stare dietro qualunque proxy (o nessuno, in una rete
    /// interna) li mette lei.
    /// </summary>
    /// <remarks>
    /// La Content-Security-Policy ammette solo script serviti da qui: è la
    /// protezione che rende inutile a uno script iniettato l'access token
    /// tenuto in memoria. Gli stili inline servono a Radix, che posiziona
    /// menu e finestre con l'attributo style; il QR della 2FA è un'immagine data:.
    /// Le immagini di Apple (mzstatic) e Google (googleusercontent) sono gli
    /// screenshot della pagina dello store, mostrati così come li serve lo store.
    /// </remarks>
    public static void UseSecurityHeaders(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            var h = http.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["X-Frame-Options"] = "DENY";
            h["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data: https://*.mzstatic.com https://*.googleusercontent.com; " +
                "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            await next();
        });
    }

    /// <summary>
    /// I file con cui TikTok, Meta o Google verificano che il sito è nostro
    /// (<c>tiktok….txt</c>, <c>google….html</c>…): stanno in
    /// <c>{Reports}/_verify</c>, nel volume dei dati, e rispondono a qualunque
    /// percorso che finisca con il loro nome, perché ogni piattaforma sceglie
    /// il suo (TikTok lo vuole sotto il prefisso indicato nel suo portale).
    /// </summary>
    /// <remarks>
    /// Solo nomi semplici (lettere, numeri, trattini) con estensione .txt o
    /// .html: nessun percorso arriva al disco così com'è.
    /// </remarks>
    public static void UseSiteVerificationFiles(this WebApplication app)
    {
        var directory = Path.Combine(app.Configuration["Reports:StorageDirectory"] ?? "/data/reports", "_verify");
        app.Use(async (http, next) =>
        {
            var name = Path.GetFileName(http.Request.Path.Value ?? "");
            // Anche HEAD: alcuni verificatori chiedono prima solo le intestazioni.
            var method = http.Request.Method;
            if ((HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) && VerificationName().IsMatch(name) && File.Exists(Path.Combine(directory, name)))
            {
                var path = Path.Combine(directory, name);
                http.Response.ContentType = name.EndsWith(".html") ? "text/html; charset=utf-8" : "text/plain; charset=utf-8";
                http.Response.ContentLength = new FileInfo(path).Length;
                if (HttpMethods.IsGet(method)) await http.Response.SendFileAsync(path);
                return;
            }
            await next();
        });
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_-]{4,100}\.(txt|html)$")]
    private static partial System.Text.RegularExpressions.Regex VerificationName();

    /// <summary>
    /// Serve il pannello compilato da <c>wwwroot</c>. In sviluppo la cartella
    /// non c'è: il pannello lo serve Vite, e qui non si fa niente.
    /// </summary>
    public static void MapFrontend(this WebApplication app)
    {
        var root = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        if (!File.Exists(Path.Combine(root, "index.html"))) return;

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            // I file in /assets hanno l'hash nel nome: si tengono in cache per
            // sempre. index.html no, altrimenti dopo un aggiornamento il
            // browser chiederebbe file che non esistono più.
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
                ctx.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache"
        });

        // Le rotte del pannello (/o/…/projects) non sono file: ricevono
        // index.html. Quelle dell'API che non esistono restano 404.
        app.MapFallback(async http =>
        {
            if (http.Request.Path.StartsWithSegments("/api"))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            http.Response.Headers.CacheControl = "no-cache";
            http.Response.ContentType = "text/html; charset=utf-8";
            await http.Response.SendFileAsync(Path.Combine(root, "index.html"));
        });
    }
}
