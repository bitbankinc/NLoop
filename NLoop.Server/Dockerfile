FROM mcr.microsoft.com/dotnet/aspnet:6.0-bullseye-slim-amd64 AS base
WORKDIR /app
EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000

# Creates a non-root user with an explicit UID and adds permission to access the /app folder
# For more info, please refer to https://aka.ms/vscode-docker-dotnet-configure-containers
RUN adduser -u 5678 --disabled-password --gecos "" appuser && chown -R appuser /app
USER appuser

FROM mcr.microsoft.com/dotnet/sdk:6.0-bullseye-slim-amd64 AS build
WORKDIR /src

ENV DOTNET_CLI_TELEMETRY_OPTOUT 1

# 1. restore tools
COPY [".config/dotnet-tools.json", ".config/dotnet-tools.json"]
RUN dotnet tool restore

# 2. restore dependencies
COPY ["BoltzClient/BoltzClient.fsproj", "BoltzClient/"]
COPY ["LndGrpcClient/LndGrpcClient.csproj", "LndGrpcClient/"]
COPY ["LndClient/LndClient.fsproj", "LndClient/"]
COPY ["NLoop.OpenAPIClient/NLoop.OpenAPIClient.csproj", "NLoop.OpenAPIClient/"]
COPY ["NLoop.Domain/NLoop.Domain.fsproj", "NLoop.Domain/"]
COPY ["NLoop.Server/NLoop.Server.fsproj", "NLoop.Server/"]
COPY ["Directory.Build.props", "Directory.Build.props"]

RUN dotnet restore "NLoop.Server/NLoop.Server.fsproj"

# 3. Build
COPY . .
WORKDIR "/src/NLoop.Server"
RUN dotnet build "NLoop.Server.fsproj" --no-restore -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "NLoop.Server.fsproj" -c Release \
    -o /app/publish \
    -p:PublishReadyToRun=true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=false \
    -p:RuntimeIdentifier=linux-x64 \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    --self-contained true

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["./nloopd"]
