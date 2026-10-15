FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled@sha256:84def277556c2843a44fdeefbd3145d0a2e32d8f736d0c1a7b0a96b2880b0187
WORKDIR /app
COPY --from=build /app ./

# The Web deployment sets ASPNETCORE_HTTP_PORTS to "8080;8081"
ENV ASPNETCORE_HTTP_PORTS="8080" \
    DOTNET_EnableDiagnostics=0

USER 1654:1654
EXPOSE 8080
HEALTHCHECK NONE
ENTRYPOINT ["dotnet", "/app/AgentPlatform.Host.dll"]
