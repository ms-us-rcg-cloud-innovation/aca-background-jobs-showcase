# syntax=docker/dockerfile:1
# Build context must be the repository root.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY NuGet.config ./
COPY src/Jobs.Shared/Jobs.Shared.csproj src/Jobs.Shared/
COPY src/Worker.ManualJob/Worker.ManualJob.csproj src/Worker.ManualJob/
RUN dotnet restore src/Worker.ManualJob/Worker.ManualJob.csproj
COPY src/ src/
RUN dotnet publish src/Worker.ManualJob/Worker.ManualJob.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app
COPY --from=build /app ./
 ENTRYPOINT ["dotnet", "Worker.ManualJob.dll"]