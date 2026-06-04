var builder = DistributedApplication.CreateBuilder(args);

var neo4j = builder.AddContainer("neo4j", "neo4j", "5.26")
    .WithEnvironment("NEO4J_AUTH", "neo4j/password")
    .WithHttpEndpoint(name: "browser", port: 7474, targetPort: 7474, isProxied: false)
    .WithEndpoint(name: "bolt", port: 7687, targetPort: 7687, isProxied: false);

builder.AddProject<Projects.AspireDemo_DemoApp>("demoapp")
    .WaitFor(neo4j)
    .WithEnvironment("Neo4j__Uri", "bolt://localhost:7687")
    .WithEnvironment("Neo4j__Username", "neo4j")
    .WithEnvironment("Neo4j__Password", "password")
    .WithEnvironment("Neo4j__Database", "neo4j");

builder.Build().Run();
