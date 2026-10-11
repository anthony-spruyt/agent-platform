using System.Security.Cryptography;

var builder = DistributedApplication.CreateBuilder(args);

const string LocalSessionTokenKey = "local-development-only-session-key";
const string LocalWebhookSecret = "local-development-only-webhook-secret";
const string TemporalNamespace = "agent-platform";

using var githubAppKey = RSA.Create(2048);

var rundata = builder.AddPostgres("postgres").AddDatabase("rundata");

var temporal = builder.AddContainer("temporal", "temporalio/temporal", "1.9.1")
    .WithArgs("server", "start-dev", "--ip", "0.0.0.0", "--namespace", TemporalNamespace)
    .WithEndpoint(port: 7233, targetPort: 7233, name: "grpc", scheme: "tcp")
    .WithHttpEndpoint(port: 8233, targetPort: 8233, name: "ui");

var temporalGrpc = temporal.GetEndpoint("grpc");
var temporalAddress = ReferenceExpression.Create($"{temporalGrpc.Property(EndpointProperty.HostAndPort)}");

var migrate = builder.AddProject<Projects.AgentPlatform_Host>("migrate")
    .WithEnvironment("AgentPlatform__Role", "Migrate")
    .WithReference(rundata)
    .WaitFor(rundata);

builder.AddProject<Projects.AgentPlatform_Host>("web")
    .WithEnvironment("AgentPlatform__Role", "Web")
    .WithEnvironment("Temporal__Namespace", TemporalNamespace)
    .WithEnvironment("AgentGateway__SessionTokenKey", LocalSessionTokenKey)
    .WithEnvironment("GitHub__WebhookSecret", LocalWebhookSecret)
    .WithEnvironment("Temporal__Address", temporalAddress)
    .WithHttpEndpoint(targetPort: 8080, name: "webhooks")
    .WithHttpEndpoint(targetPort: 8081, name: "mcp")
    .WithHttpHealthCheck("/health", endpointName: "webhooks")
    .WaitFor(temporal);

builder.AddProject<Projects.AgentPlatform_Host>("worker")
    .WithEnvironment("AgentPlatform__Role", "Worker")
    .WithEnvironment("AgentGateway__SessionTokenKey", LocalSessionTokenKey)
    .WithEnvironment("GitHub__AppId", "1")
    .WithEnvironment("GitHub__PrivateKey", githubAppKey.ExportRSAPrivateKeyPem())
    .WithEnvironment("Temporal__Address", temporalAddress)
    .WithEnvironment("Temporal__Namespace", TemporalNamespace)
    .WithReference(rundata)
    .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS")
    .WithHttpHealthCheck("/health")
    .WaitFor(temporal)
    .WaitForCompletion(migrate);

builder.Build().Run();
