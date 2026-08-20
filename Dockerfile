# syntax=docker/dockerfile:1.7

FROM node:24-alpine AS client-build
WORKDIR /client
COPY client/reach-commander-ui/package.json client/reach-commander-ui/package-lock.json ./
RUN npm ci
COPY client/reach-commander-ui/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS server-build
WORKDIR /src
COPY global.json Directory.Build.props ReachCommander.slnx ./
COPY src/ReachCommander.Domain/ReachCommander.Domain.csproj src/ReachCommander.Domain/
COPY src/ReachCommander.Application/ReachCommander.Application.csproj src/ReachCommander.Application/
COPY src/ReachCommander.ArchiveProtocol/ReachCommander.ArchiveProtocol.csproj src/ReachCommander.ArchiveProtocol/
COPY src/ReachCommander.ArchiveWorker/ReachCommander.ArchiveWorker.csproj src/ReachCommander.ArchiveWorker/
COPY src/ReachCommander.Infrastructure/ReachCommander.Infrastructure.csproj src/ReachCommander.Infrastructure/
COPY src/ReachCommander.Api/ReachCommander.Api.csproj src/ReachCommander.Api/
RUN dotnet restore src/ReachCommander.Api/ReachCommander.Api.csproj
COPY src/ src/
COPY --from=client-build /client/dist/reach-commander-ui/browser/ src/ReachCommander.Api/wwwroot/
RUN dotnet publish src/ReachCommander.Api/ReachCommander.Api.csproj --configuration Release --no-restore --output /app/publish -p:BuildAngularOnPublish=false
RUN test -f /app/publish/archive-worker/ReachCommander.ArchiveWorker.dll

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    ReachCommander__SourcesPath=/config/sources.json
EXPOSE 8080
COPY --from=server-build --chown=1000:1000 /app/publish/ ./
RUN mkdir -p /host/proc/net /host/sys
USER 1000:1000
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
  CMD wget --quiet --tries=1 --spider http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["dotnet", "ReachCommander.Api.dll"]
