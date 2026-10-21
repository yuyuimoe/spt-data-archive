# Build context is the repository root:  docker build -f src/SptArchive.Archiver/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, so this layer is cached until a .csproj changes.
# Directory.Packages.props carries every package version (central package management, TD-07): without it
# the restore fails with NU1015, "do not have a version specified".
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json ./
COPY src/SptArchive.Core/SptArchive.Core.csproj src/SptArchive.Core/
COPY src/SptArchive.Archiver/SptArchive.Archiver.csproj src/SptArchive.Archiver/
RUN dotnet restore src/SptArchive.Archiver/SptArchive.Archiver.csproj

COPY src/SptArchive.Core src/SptArchive.Core
COPY src/SptArchive.Archiver src/SptArchive.Archiver
RUN dotnet publish src/SptArchive.Archiver/SptArchive.Archiver.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "SptArchive.Archiver.dll"]
