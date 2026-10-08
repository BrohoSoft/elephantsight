# Il frontend non ha un server suo: si compila e lo serve Caddy, lo stesso
# che fa da proxy all'API. Questa immagine è quindi Caddy con dentro la build.

FROM --platform=$BUILDPLATFORM node:22-alpine AS build
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM caddy:2.10-alpine
COPY --from=build /src/dist /srv
