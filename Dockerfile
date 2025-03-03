# Используем официальный образ .NET SDK для сборки
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
WORKDIR /app

# Копируем файл решения и восстанавливаем зависимости
COPY *.sln ./
COPY ConsoleApp1/*.csproj ./ConsoleApp1/
RUN dotnet restore

# Копируем все файлы и собираем проект
COPY . ./
RUN dotnet publish -c Release -o out

# Используем runtime образ
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Устанавливаем Tesseract OCR и языковые пакеты
RUN apt-get update && apt-get install -y tesseract-ocr tesseract-ocr-rus libtesseract-dev

# Копируем собранный проект
COPY --from=build-env /app/out .

# Указываем рабочий путь к Tesseract (если нужно)
ENV TESSDATA_PREFIX=/usr/share/tesseract-ocr/4.00/tessdata/

# Запускаем приложение
ENTRYPOINT ["dotnet", "ConsoleApp1.dll"]
