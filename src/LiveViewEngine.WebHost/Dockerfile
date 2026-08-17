FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/LiveViewEngine.WebHost/LiveViewEngine.WebHost.csproj", "src/LiveViewEngine.WebHost/"]
COPY ["src/LiveViewEngine.Core/LiveViewEngine.Core.csproj", "src/LiveViewEngine.Core/"]
RUN dotnet restore "src/LiveViewEngine.WebHost/LiveViewEngine.WebHost.csproj"

COPY . .
WORKDIR "/src/src/LiveViewEngine.WebHost"
RUN dotnet publish "LiveViewEngine.WebHost.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "LiveViewEngine.WebHost.dll"]
