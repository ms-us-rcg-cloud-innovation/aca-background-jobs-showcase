# syntax=docker/dockerfile:1
# Build context must be the repository root.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY NuGet.config ./
COPY src/Jobs.Shared/Jobs.Shared.csproj src/Jobs.Shared/
COPY src/Producer.Api/Producer.Api.csproj src/Producer.Api/
RUN dotnet restore src/Producer.Api/Producer.Api.csproj
COPY src/ src/
RUN dotnet publish src/Producer.Api/Producer.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
 ENTRYPOINT ["dotnet", "Producer.Api.dll"]