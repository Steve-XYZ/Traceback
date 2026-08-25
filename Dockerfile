FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Traceback.Domain/Traceback.Domain.csproj src/Traceback.Domain/
COPY src/Traceback.Connectors.Abstractions/Traceback.Connectors.Abstractions.csproj src/Traceback.Connectors.Abstractions/
COPY src/Traceback.Application/Traceback.Application.csproj src/Traceback.Application/
COPY src/Traceback.Infrastructure/Traceback.Infrastructure.csproj src/Traceback.Infrastructure/
COPY src/Traceback.Connectors.Fixtures/Traceback.Connectors.Fixtures.csproj src/Traceback.Connectors.Fixtures/
COPY src/Traceback.Api/Traceback.Api.csproj src/Traceback.Api/
RUN dotnet restore src/Traceback.Api/Traceback.Api.csproj

COPY . .
RUN dotnet publish src/Traceback.Api/Traceback.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Traceback.Api.dll"]
