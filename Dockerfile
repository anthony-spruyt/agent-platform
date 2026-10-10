FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled@sha256:48e51f2f6798897be7ac4e775c049ed8fe60d3190f637e1f9c9dc7513efa659c
WORKDIR /app
COPY --from=build /app ./

# The Web deployment sets ASPNETCORE_HTTP_PORTS to "8080;8081"
ENV ASPNETCORE_HTTP_PORTS="8080" \
    DOTNET_EnableDiagnostics=0

USER 1654:1654
EXPOSE 8080
HEALTHCHECK NONE
ENTRYPOINT ["dotnet", "/app/AgentPlatform.Host.dll"]
