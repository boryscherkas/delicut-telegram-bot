# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

# Copy csproj and restore
COPY DelicutTelegramBot/DelicutTelegramBot/DelicutTelegramBot.csproj DelicutTelegramBot/DelicutTelegramBot/
RUN dotnet restore DelicutTelegramBot/DelicutTelegramBot/DelicutTelegramBot.csproj

# Copy everything and publish
COPY DelicutTelegramBot/ DelicutTelegramBot/
RUN dotnet publish DelicutTelegramBot/DelicutTelegramBot/DelicutTelegramBot.csproj \
    -c Release -o /app --no-restore \
    /p:PublishTrimmed=false

# Runtime stage — smallest possible image
FROM mcr.microsoft.com/dotnet/runtime:9.0-alpine AS runtime
WORKDIR /app

# Install ICU for globalization (needed for .NET on Alpine)
RUN apk add --no-cache icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

COPY --from=build /app .

ENTRYPOINT ["dotnet", "DelicutTelegramBot.dll"]
