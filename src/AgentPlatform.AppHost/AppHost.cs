var builder = DistributedApplication.CreateBuilder(args);

var temporal = builder.AddContainer("temporal", "temporalio/temporal", "1.9.1")
    .WithArgs("server", "start-dev", "--ip", "0.0.0.0")
    .WithEndpoint(port: 7233, targetPort: 7233, name: "grpc", scheme: "tcp")
    .WithHttpEndpoint(port: 8233, targetPort: 8233, name: "ui");

var temporalGrpc = temporal.GetEndpoint("grpc");

builder.AddProject<Projects.AgentPlatform_Host>("web")
    .WithEnvironment("AgentPlatform__Role", "Web")
    .WithHttpEndpoint(name: "webhooks")
    .WithHttpEndpoint(name: "mcp")
    .WithHttpHealthCheck("/health", endpointName: "webhooks");

builder.AddProject<Projects.AgentPlatform_Host>("worker")
    .WithEnvironment("AgentPlatform__Role", "Worker")
    .WithEnvironment("Temporal__Address", ReferenceExpression.Create($"{temporalGrpc.Property(EndpointProperty.HostAndPort)}"))
    .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS")
    .WithHttpHealthCheck("/health")
    .WaitFor(temporal);

builder.Build().Run();
