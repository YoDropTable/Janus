FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Janus.Server/Janus.Server.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
LABEL org.opencontainers.image.source="https://github.com/Sofic-ai/Janus"
LABEL org.opencontainers.image.description="MCP-first, self-hosted knowledge system for physical assets"
VOLUME ["/data"]
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Janus.Server.dll"]
