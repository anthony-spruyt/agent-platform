FROM mcr.microsoft.com/dotnet/sdk:10.0.404-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/.editorconfig src/
COPY src/AgentPlatform.ServiceDefaults/AgentPlatform.ServiceDefaults.csproj src/AgentPlatform.ServiceDefaults/
COPY src/AgentPlatform.Host/AgentPlatform.Host.csproj src/AgentPlatform.Host/
RUN dotnet restore src/AgentPlatform.Host/AgentPlatform.Host.csproj --runtime linux-x64

COPY src/AgentPlatform.ServiceDefaults/ src/AgentPlatform.ServiceDefaults/
COPY src/AgentPlatform.Host/ src/AgentPlatform.Host/
RUN dotnet publish src/AgentPlatform.Host/AgentPlatform.Host.csproj \
      --configuration Release --no-restore --runtime linux-x64 --self-contained false --output /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble-chiseled@sha256:7c625e44e5d588b4d7b2a913e94ca4bd5098ce47442da404829e4c7936a764e1
WORKDIR /app
COPY --from=build /app ./

# The Web deployment sets ASPNETCORE_HTTP_PORTS to "8080;8081"
ENV ASPNETCORE_HTTP_PORTS="8080" \
    DOTNET_EnableDiagnostics=0

USER 1654:1654
EXPOSE 8080
HEALTHCHECK NONE
ENTRYPOINT ["dotnet", "/app/AgentPlatform.Host.dll"]
