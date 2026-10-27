FROM mcr.microsoft.com/dotnet/runtime:6.0 AS base
WORKDIR /app

RUN groupadd --gid 1654 app && useradd --uid 1654 --gid app --create-home app
USER app
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:6.0 AS build
ARG configuration=Release
WORKDIR /src
COPY ["DubboNet.csproj", "."]
COPY ["Tests/DubboClientIntegrationTests/DubboClientIntegrationTests.csproj", "Tests/DubboClientIntegrationTests/"]
RUN dotnet restore "Tests/DubboClientIntegrationTests/DubboClientIntegrationTests.csproj"
COPY . .
WORKDIR "/src/Tests/DubboClientIntegrationTests"
RUN dotnet build "DubboClientIntegrationTests.csproj" -c $configuration -o /app/build

FROM build AS publish
ARG configuration=Release
RUN dotnet publish "DubboClientIntegrationTests.csproj" -c $configuration -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "DubboClientIntegrationTests.dll"]
