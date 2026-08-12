# Build the MCP host as a self-contained container image.
#
# Built from the repository root so the ProjectReferences resolve:
#   docker build -f tools/AgentMemory.McpHost/Dockerfile -t agent-memory-mcp .
#
# The image defaults to the HTTP transport. stdio is for a client that launches the process itself,
# which is the `dotnet tool install -g` path, not this one -- a container speaking stdio would need
# `docker run -i` and an attached client, and getting that subtly wrong presents as a silent hang.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY README.md ./
COPY resources/ ./resources/
COPY src/ ./src/
COPY tools/AgentMemory.McpHost/ ./tools/AgentMemory.McpHost/

RUN dotnet publish tools/AgentMemory.McpHost/AgentMemory.McpHost.csproj \
    -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .

# Bind to every interface: localhost inside a container is reachable only from inside it, which is the
# most common way a correctly built image looks broken from the host.
ENV AGENT_MEMORY_MCP_TRANSPORT=http \
    AGENT_MEMORY_MCP_URL=http://0.0.0.0:5233
EXPOSE 5233

# Non-root by default. This process needs no write access to its own filesystem.
USER $APP_UID

ENTRYPOINT ["dotnet", "AgentMemory.McpHost.dll"]
