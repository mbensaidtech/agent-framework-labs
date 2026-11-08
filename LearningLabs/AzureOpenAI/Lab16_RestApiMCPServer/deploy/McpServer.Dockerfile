# The MCP server of the provider team: its own image, its own release cycle. It only needs the URL and an API key of the
# microservice (environment variables). Build context: the root of the repository (the lab project references CommonUtilities).
# Use the Solution, or your Start once completed: docker compose build --build-arg PROJECT=Start
ARG PROJECT=Solution
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
COPY CommonUtilities/ CommonUtilities/
COPY LearningLabs/AzureOpenAI/Lab16_RestApiMCPServer/${PROJECT}/ LearningLabs/AzureOpenAI/Lab16_RestApiMCPServer/${PROJECT}/
RUN dotnet publish LearningLabs/AzureOpenAI/Lab16_RestApiMCPServer/${PROJECT}/RestApiMCPServer.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Serve MCP clients (no scenario), listen on 8080 in the container
ENV OrdersMcpServer__Serve=true \
    OrdersMcpServer__ListenUrls=http://+:8080 \
    OrdersApi__StartIfNotRunning=false
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "RestApiMCPServer.dll"]
