# Il worker: stessa ricetta dell'API, un altro progetto.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG TARGETARCH

COPY Flarelytics.Core/Flarelytics.Core.csproj Flarelytics.Core/
COPY Flarelytics.Worker/Flarelytics.Worker.csproj Flarelytics.Worker/
RUN dotnet restore Flarelytics.Worker/Flarelytics.Worker.csproj -a $TARGETARCH

COPY Flarelytics.Core/ Flarelytics.Core/
COPY Flarelytics.Worker/ Flarelytics.Worker/
RUN dotnet publish Flarelytics.Worker/Flarelytics.Worker.csproj -c Release -a $TARGETARCH --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# libgssapi per Npgsql (vedi il Dockerfile dell'API); tzdata perché i report
# di Apple seguono il giorno del Pacifico e il worker deve sapere che giorno
# è a Cupertino.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 tzdata \
    && rm -rf /var/lib/apt/lists/*

# Stesso utente dell'API: deve leggere i file cifrati che l'API ha scritto.
RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin flarelytics \
    && mkdir -p /var/lib/flarelytics/reports \
    && chown -R 10001 /var/lib/flarelytics
USER 10001

COPY --from=build /app .

ENV DOTNET_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "Flarelytics.Worker.dll"]
