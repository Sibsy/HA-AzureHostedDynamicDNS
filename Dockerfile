FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS publish


WORKDIR /src
COPY ["Azure Hosted Dynamic DNS.csproj", "."]
RUN dotnet restore "./Azure Hosted Dynamic DNS.csproj" --runtime linux-musl-x64
COPY . .

RUN dotnet publish "./Azure Hosted Dynamic DNS.csproj" -c Release -o /app/publish --no-restore --self-contained true /p:PublishTrimmed=true /p:PublishSingleFile=true --runtime linux-musl-x64


FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-alpine AS final

RUN adduser --disabled-password --home /app --gecos '' dotnetuser && chown -R dotnetuser /app
USER dotnetuser

WORKDIR /app
COPY --from=publish /app/publish .

ENTRYPOINT ["./Azure Hosted Dynamic DNS"]