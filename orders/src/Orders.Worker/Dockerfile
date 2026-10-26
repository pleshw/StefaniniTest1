# Build context is the repository root: docker build -f orders/src/Orders.Worker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json .editorconfig Directory.Build.props Directory.Packages.props ./
COPY shared/ shared/
COPY orders/ orders/
# Orders.Infrastructure generates its Catalog client from this spec at build time.
COPY catalog/openapi/ catalog/openapi/
RUN dotnet publish orders/src/Orders.Worker/Orders.Worker.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Orders.Worker.dll"]
