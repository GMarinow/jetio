FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# jq and zip are used by scripts/package-plugin.sh below.
RUN apt-get update \
 && apt-get install -y --no-install-recommends jq zip \
 && rm -rf /var/lib/apt/lists/*

COPY nuget.config version.json ./
COPY src/Jetio/Jetio.csproj src/Jetio/
COPY src/Jellyfin.Plugin.Jetio/Jellyfin.Plugin.Jetio.csproj src/Jellyfin.Plugin.Jetio/
RUN dotnet restore src/Jetio/Jetio.csproj \
 && dotnet restore src/Jellyfin.Plugin.Jetio/Jellyfin.Plugin.Jetio.csproj

COPY scripts/ scripts/
COPY src/ src/

# Package the plugin into jetio's wwwroot so jetio can serve it as a Jellyfin repository.
# Done before publishing jetio so it lands in the published output, and via the shared script
# so the served package always carries the version from version.json.
RUN mkdir -p src/Jetio/wwwroot/plugin \
 && bash scripts/package-plugin.sh /src/src/Jetio/wwwroot/plugin

RUN dotnet publish src/Jetio/Jetio.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

COPY --from=build /app .

# /library is the .strm tree Jellyfin reads; /config holds jetio.json and library.json.
VOLUME ["/library", "/config"]
EXPOSE 9000

ENV DOTNET_gcServer=0

ENTRYPOINT ["dotnet", "Jetio.dll"]
