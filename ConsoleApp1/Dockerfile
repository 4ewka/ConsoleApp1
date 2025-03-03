# Используем официальный образ .NET
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build-env
WORKDIR /app

# Копируем файлы проекта и восстанавливаем зависимости
COPY *.csproj ./
RUN dotnet restore

# Копируем все файлы и собираем проект
COPY . ./
RUN dotnet publish -c Release -o out

# Используем runtime образ
FROM mcr.microsoft.com/dotnet/aspnet:6.0
WORKDIR /app
COPY --from=build-env /app/out .
ENTRYPOINT ["dotnet", "ConsoleApp1.dll"]