# Build context is the repository root: docker build -f orders/src/Orders.Infrastructure/Migrator.Dockerfile .
# Same publish as Orders.Api, started with the "migrate" argument: applies the EF Core migrations, creates
# Wolverine's tables in the wolverine schema (resources setup) and exits.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY shared/ shared/
COPY orders/ orders/
RUN dotnet publish orders/src/Orders.Api/Orders.Api.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Orders.Api.dll", "migrate"]
