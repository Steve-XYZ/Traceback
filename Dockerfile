FROM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Traceback.Domain/Traceback.Domain.csproj src/Traceback.Domain/
COPY src/Traceback.Connectors.Abstractions/Traceback.Connectors.Abstractions.csproj src/Traceback.Connectors.Abstractions/
COPY src/Traceback.Application/Traceback.Application.csproj src/Traceback.Application/
COPY src/Traceback.Infrastructure/Traceback.Infrastructure.csproj src/Traceback.Infrastructure/
COPY src/Traceback.Connectors.Fixtures/Traceback.Connectors.Fixtures.csproj src/Traceback.Connectors.Fixtures/
COPY src/Traceback.Connectors.GitHub/Traceback.Connectors.GitHub.csproj src/Traceback.Connectors.GitHub/
COPY src/Traceback.Api/Traceback.Api.csproj src/Traceback.Api/
RUN dotnet restore src/Traceback.Api/Traceback.Api.csproj

COPY . .
RUN dotnet publish src/Traceback.Api/Traceback.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# The .NET 10 ASP.NET image provides the unprivileged app user and UID.
USER $APP_UID

ENTRYPOINT ["dotnet", "Traceback.Api.dll"]
