FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY src/InternalAssetLibrary.Contracts/ src/InternalAssetLibrary.Contracts/
COPY src/InternalAssetLibrary.Core/ src/InternalAssetLibrary.Core/
COPY src/InternalAssetLibrary.Server/ src/InternalAssetLibrary.Server/
RUN dotnet restore src/InternalAssetLibrary.Server --locked-mode
RUN dotnet publish src/InternalAssetLibrary.Server -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates ffmpeg \
    && rm -rf /var/lib/apt/lists/* && mkdir -p /data /updates && chown -R app:app /data /updates /app
COPY --from=build --chown=app:app /app/publish/ ./
USER app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 ASPNETCORE_ENVIRONMENT=Production DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "InternalAssetLibrary.Server.dll"]
