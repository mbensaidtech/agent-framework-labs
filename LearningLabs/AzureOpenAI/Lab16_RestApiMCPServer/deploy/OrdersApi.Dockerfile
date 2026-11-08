# The REST microservice of the provider team: built and deployed by its own pipeline, independently of the MCP server.
# Build context: the root of the repository (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY LearningLabs/AzureOpenAI/Lab16_RestApiMCPServer/OrdersApi/ OrdersApi/
RUN dotnet publish OrdersApi/OrdersApi.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV Urls=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "OrdersApi.dll"]
