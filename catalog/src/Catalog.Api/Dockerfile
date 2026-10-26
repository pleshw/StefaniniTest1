# Build context is the repository root: docker build -f catalog/src/Catalog.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json .editorconfig Directory.Build.props Directory.Packages.props ./
COPY shared/ shared/
COPY catalog/ catalog/
RUN dotnet publish catalog/src/Catalog.Api/Catalog.Api.csproj -c Release -o /app /p:UseAppHost=false /p:OpenApiGenerateDocumentsOnBuild=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Catalog.Api.dll"]
