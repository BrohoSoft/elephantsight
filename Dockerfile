# ElephantSight in un'immagine sola: API, sincronizzazione con gli store e
# pannello. Accanto serve solo PostgreSQL (vedi compose.yaml).

# 1. Il pannello: file statici.
FROM --platform=$BUILDPLATFORM node:22-alpine AS web
WORKDIR /web
COPY Flarelytics.Web/package.json Flarelytics.Web/package-lock.json ./
RUN npm ci
COPY Flarelytics.Web/ ./
RUN npm run build

# 2. L'applicazione .NET.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG TARGETARCH
COPY Flarelytics.Core/Flarelytics.Core.csproj Flarelytics.Core/
COPY Flarelytics.Api/Flarelytics.Api.csproj Flarelytics.Api/
RUN dotnet restore Flarelytics.Api/Flarelytics.Api.csproj -a $TARGETARCH
COPY Flarelytics.Core/ Flarelytics.Core/
COPY Flarelytics.Api/ Flarelytics.Api/
RUN dotnet publish Flarelytics.Api/Flarelytics.Api.csproj -c Release -a $TARGETARCH --no-restore -o /app /p:UseAppHost=false

# 3. Il runtime.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# libgssapi: Npgsql la cerca a ogni connessione e, senza, riempie il log di
# errori innocui. tzdata: i report di Apple seguono il giorno del Pacifico.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 tzdata \
    && rm -rf /var/lib/apt/lists/*

# Non come root. /data esiste già con il proprietario giusto: un volume
# montato lì sopra la prima volta ne eredita i permessi.
RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin flarelytics \
    && mkdir -p /data/keys /data/secrets /data/reports \
    && chown -R 10001 /data && chmod 700 /data/keys /data/secrets
USER 10001

COPY --from=build /app .
COPY --from=web /web/dist ./wwwroot

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    Secrets__CreateKeyIfMissing=true

EXPOSE 8080
VOLUME ["/data"]
HEALTHCHECK --interval=30s --timeout=3s --start-period=40s CMD bash -c '</dev/tcp/127.0.0.1/8080' || exit 1
ENTRYPOINT ["dotnet", "Flarelytics.Api.dll"]
