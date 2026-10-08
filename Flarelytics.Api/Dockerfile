# Due stadi: si compila con l'SDK, si esegue con il solo runtime. L'immagine
# finale non contiene né compilatore né sorgenti.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG TARGETARCH

# Prima i soli file di progetto: se non cambiano, il ripristino dei pacchetti
# arriva dalla cache anche quando cambia il codice.
COPY Flarelytics.Core/Flarelytics.Core.csproj Flarelytics.Core/
COPY Flarelytics.Api/Flarelytics.Api.csproj Flarelytics.Api/
RUN dotnet restore Flarelytics.Api/Flarelytics.Api.csproj -a $TARGETARCH

COPY Flarelytics.Core/ Flarelytics.Core/
COPY Flarelytics.Api/ Flarelytics.Api/
RUN dotnet publish Flarelytics.Api/Flarelytics.Api.csproj -c Release -a $TARGETARCH --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Npgsql prova Kerberos a ogni connessione: senza questa libreria funziona lo
# stesso, ma scrive un errore a ogni avvio, e un errore che si impara a
# ignorare nasconde quelli veri.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

# Non come root. La cartella dei segreti esiste già nell'immagine con il
# proprietario giusto: un volume Docker montato lì sopra la prima volta ne
# eredita i permessi, e il processo ci può scrivere.
RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin flarelytics \
    && mkdir -p /var/lib/flarelytics/secrets /var/lib/flarelytics/reports \
    && chown -R 10001 /var/lib/flarelytics \
    && chmod 700 /var/lib/flarelytics/secrets
USER 10001

COPY --from=build /app .

ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "Flarelytics.Api.dll", "--urls", "http://0.0.0.0:8080"]
