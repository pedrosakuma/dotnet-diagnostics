# syntax=docker/dockerfile:1
# Dockerfile for BadCodeSample.
# Build from repo root:
#   docker build --secret id=nugetconfig,src="$HOME/.nuget/NuGet/NuGet.Config" \
#     -t badcode-sample:dev -f samples/BadCodeSample/Dockerfile .

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1 \
    DOTNET_NOLOGO=1

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY samples/Directory.Build.props samples/
COPY samples/BadCodeSample/ samples/BadCodeSample/

RUN --mount=type=secret,id=nugetconfig,target=/run/secrets/NuGet.Config,required=true \
    dotnet restore samples/BadCodeSample/BadCodeSample.csproj \
      --configfile /run/secrets/NuGet.Config \
 && dotnet publish samples/BadCodeSample/BadCodeSample.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=1 \
    DOTNET_NOLOGO=1

COPY --from=build /app/publish ./

EXPOSE 8080
ENTRYPOINT ["dotnet", "BadCodeSample.dll"]
