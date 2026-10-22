FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
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

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled@sha256:5165aeeb688df15e869a56a7584bb94bf16a14e0b3ba77b49fcdbe350f6b8020
WORKDIR /app
COPY --from=build /app ./

# The Web deployment sets ASPNETCORE_HTTP_PORTS to "8080;8081"
ENV ASPNETCORE_HTTP_PORTS="8080" \
    DOTNET_EnableDiagnostics=0

USER 1654:1654
EXPOSE 8080
HEALTHCHECK NONE
ENTRYPOINT ["dotnet", "/app/AgentPlatform.Host.dll"]
