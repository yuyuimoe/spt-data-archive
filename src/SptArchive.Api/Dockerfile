# Build context is the repository root:  docker build -f src/SptArchive.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, so this layer is cached until a .csproj changes.
# Directory.Packages.props carries every package version (central package management, TD-07): without it
# the restore fails with NU1015, "do not have a version specified".
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json ./
COPY src/SptArchive.Core/SptArchive.Core.csproj src/SptArchive.Core/
COPY src/SptArchive.Api/SptArchive.Api.csproj src/SptArchive.Api/
RUN dotnet restore src/SptArchive.Api/SptArchive.Api.csproj

COPY src/SptArchive.Core src/SptArchive.Core
COPY src/SptArchive.Api src/SptArchive.Api
RUN dotnet publish src/SptArchive.Api/SptArchive.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SptArchive.Api.dll"]
