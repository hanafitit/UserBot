# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /build

# Copy project files
COPY TestApp.csproj .
RUN dotnet restore

# Copy source code
COPY . .

# Build
RUN dotnet build -c Release --no-restore

# Publish
RUN dotnet publish -c Release -o /app --no-build

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:8.0

WORKDIR /app

# Copy published app
COPY --from=build /app .

# Run the application
ENTRYPOINT ["dotnet", "TestApp.dll"]
