﻿using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Tesseract;
using System.Text;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using System.Globalization;
using OpenCvSharp;



class Program
{
    private static readonly string baseDirectory = @"C:\bot"; 
    private static readonly string usersFile = Path.Combine(baseDirectory, "users.txt");
    private static readonly string reportsDir = Path.Combine(baseDirectory, "Reports");
    private static readonly string botToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"); // Токен из переменных окружения
    private static readonly Dictionary<string, string> activeCollections = new(); // Активные сборы отчетов
    private static readonly TelegramBotClient bot = new TelegramBotClient(botToken);
    private static readonly Dictionary<long, string> pendingUserInfo = new();
    private static readonly SemaphoreSlim fileLock = new(1, 1);
    private static readonly SemaphoreSlim mediaGroupLock = new(1, 1);
    private static readonly List<string> SupportedImageFormats = new List<string>
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"
    };
    private static readonly string activeCollectionsFile = Path.Combine(baseDirectory, "active_collections.txt");
    


    static async Task Main()
    {
        var cts = new CancellationTokenSource();

        // Обрабатываем SIGTERM для корректного завершения
        AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
        {
            Console.WriteLine("Получен SIGTERM. Остановка бота...");
            cts.Cancel();
        };

        Console.WriteLine("Бот запущен...");
        await LoadActiveCollections();
        bot.StartReceiving(UpdateHandler, ErrorHandler, cancellationToken: cts.Token);


        await Task.Delay(-1, cts.Token); // Ожидание сигнала завершения
    }
    // Сохраняем активные сборы в файл
    private static async Task SaveActiveCollections()
    {
        await fileLock.WaitAsync();
        try
        {
            var lines = activeCollections.Select(kvp => $"{kvp.Key}={kvp.Value}");
            await File.WriteAllLinesAsync(activeCollectionsFile, lines);
        }
        finally
        {
            fileLock.Release();
        }
    }

    // Загружаем активные сборы из файла
    private static async Task LoadActiveCollections()
    {
        if (!File.Exists(activeCollectionsFile))
        {
            return;
        }

        var lines = await File.ReadAllLinesAsync(activeCollectionsFile);
        foreach (var line in lines)
        {
            var parts = line.Split('=');
            if (parts.Length == 2)
            {
                activeCollections[parts[0]] = parts[1];
            }
        }
    }

    // Удаляем активный сбор из файла
    private static async Task RemoveActiveCollection(string key)
    {
        await fileLock.WaitAsync();
        try
        {
            var lines = (await File.ReadAllLinesAsync(activeCollectionsFile))
                .Where(line => !line.StartsWith(key))
                .ToList();
            await File.WriteAllLinesAsync(activeCollectionsFile, lines);
        }
        finally
        {
            fileLock.Release();
        }
    }
    private static void EnsureDirectoriesExist()
    {
        // Создаем базовую папку и папку для отчетов, если их нет
        if (!Directory.Exists(baseDirectory))
            Directory.CreateDirectory(baseDirectory);
        if (!Directory.Exists(reportsDir))
            Directory.CreateDirectory(reportsDir);
        if (!File.Exists(usersFile))
            File.WriteAllText(usersFile, ""); // Создаем пустой файл users.txt, если его нет
    }

    static string ExtractText(string imagePath)
    {
        Console.WriteLine($"Начало обработки файла: {imagePath}");

        if (!File.Exists(imagePath))
        {
            Console.WriteLine($"Файл не найден: {imagePath}");
            return string.Empty;
        }

        string tessdataPath = Path.Combine(baseDirectory, "tessdata");

        if (!Directory.Exists(tessdataPath))
        {
            Console.WriteLine($"Папка с tessdata не найдена: {tessdataPath}");
            return string.Empty;
        }

        string tempPath = Path.Combine(Path.GetTempPath(), "tess_input.png");

        try
        {
            // === Подготовка изображения через OpenCvSharp ===
            Console.WriteLine($"начало подготовки изображения");
            try
            {
                using var src = Cv2.ImRead(imagePath, ImreadModes.Grayscale);

                var scaled = new Mat();
                Cv2.Resize(src, scaled, new OpenCvSharp.Size(src.Width * 5, src.Height * 5), 0, 0, InterpolationFlags.Cubic);

                var thresholded = new Mat();
                Cv2.Threshold(scaled, thresholded, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

                var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(1, 1));
                var cleaned = new Mat();
                Cv2.MorphologyEx(thresholded, cleaned, MorphTypes.Open, kernel);

                int borderSize = 60;
                Scalar white = new Scalar(255);
                var bordered = new Mat();
                Cv2.CopyMakeBorder(cleaned, bordered, borderSize, borderSize, borderSize, borderSize, BorderTypes.Constant, white);

                // Сохраняем во временный файл
                Cv2.ImWrite(tempPath, bordered);
            }
            catch (Exception ex) { Console.WriteLine($"OpenCV error: {ex.Message}"); }

            // === Обработка Tesseract ===
            Console.WriteLine($"пытаемся создать движок тессеракт");
            using var engine = new TesseractEngine(tessdataPath, "rus+eng", EngineMode.Default);
            using var img = Pix.LoadFromFile(tempPath);
            using var page = engine.Process(img, PageSegMode.SingleBlock);

            string text = page.GetText();            

            return text;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при обработке OCR: {ex.Message}");
            return string.Empty;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                    Console.WriteLine($"Файл удалён: {tempPath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при удалении временного файла: {ex.Message}");
            }
        }
    }


    private static void DeleteTempFile(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            Console.WriteLine($"Файл удален: {filePath}");
        }
    }

    static string ExtractPrice(string text)
    {
        // Регулярка ищет сумму с возможными валютами (р., p., бр.)
        Match match = Regex.Match(text, @"(?:Общая\s*\n*\s*стоимость|подпиской|Стоимость\s*\n*\s*поездки|Obuwasa CToMMOo?CTb).*?(\d+(?:[.,]\d{1,2})?)", RegexOptions.Singleline
            | RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return "Не найдено";
        }

        string amount = match.Groups[1].Value;

        // Проверяем, содержит ли сумма дробную часть
        if (!amount.Contains(",") && !amount.Contains("."))
        {
            // Пытаемся преобразовать в число
            if (int.TryParse(amount, out int number))
            {
                // Проверяем условия: число > 2000 и заканчивается на 2
                if ((number > 2000 && number % 10 == 2)||number > 4000)
                {
                    // Отбрасываем последнюю цифру
                    amount = (number / 10).ToString();
                }
            }
        }

        return amount;
    }


    private static async Task UpdateHandler(ITelegramBotClient botClient, Update update, CancellationToken token)
    {
        try
        {

            if (update.Type != UpdateType.Message || update.Message == null) return;

            var message = update.Message;
            var chatId = message.Chat.Id;
            var username = message.From?.Username;

            Console.WriteLine($"[{DateTime.Now}] Сообщение от {username} (чат {chatId}): {message.Text ?? "Нет текста"}");

            var users = LoadUsers();
            var user = users.FirstOrDefault(u => u.Username == username);
            if (user == null)
            {
                user = users.FirstOrDefault(u => u.Username == chatId.ToString());
            }

            if (message.Type == MessageType.Text && message.Text.StartsWith("/godMode"))
            {
                await HandleGodModeCommand(chatId, user, users);
                return;
            }

            // Обрабатываем только текстовые сообщения
            if (message.Type == MessageType.Text)
            {
                if (user == null && !pendingUserInfo.ContainsKey(chatId))
                {
                    await bot.SendTextMessageAsync(chatId, "Введите ваш город (Минск, Гомель, Новосибирск, Кишенев/Кишинёв, Ереван):");
                    pendingUserInfo[chatId] = "city";
                    return;
                }

                if (pendingUserInfo.ContainsKey(chatId))
                {
                    await HandleUserRegistration(chatId, message, users);
                    return;
                }

                if (message.Text.StartsWith("/start"))
                {
                    await HandleStartCommand(chatId, user);
                }
                else if (message.Text.StartsWith("/collect") && user.IsAdmin)
                {
                    await HandleCollectCommand(chatId, message.Text, user);
                }
                else if (message.Text.StartsWith("/finish") && user.IsAdmin)
                {
                    await HandleFinishCommand(chatId, user);
                }
                else if (message.Text.StartsWith("/total"))
                {
                    await HandleTotalCommand(chatId, user);
                }
                else if (message.Text.StartsWith("/info"))
                {
                    await HandleInfoCommand(chatId, user);
                }
                else if (message.Text.StartsWith("/delete"))
                {
                    await HandleDeleteCommand(chatId, users);
                }
                else if (message.Text.StartsWith("/clear"))
                {
                    await HandleClearCommand(chatId, user);
                }
                else
                {
                    // Обработка суммы чека
                    await HandleCheckAmount(chatId, message.Text, user);
                }
            }
            else
            {
                // Обработка не текстовых сообщений (фото, документы)
                await HandleNonTextMessage(chatId, message);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private static async Task HandleStartCommand(long chatId, UserInfo user)
    {
        if (user == null)
        {
            await bot.SendTextMessageAsync(chatId, "Вы не зарегистрированы. Пожалуйста, начните с команды /start.");
            return;
        }

        var message = "Ваши возможности:\n";

        if (user.IsAdmin)
        {
            message +=
                "/collect <Месяц> <Дата завершения> — начать сбор отчетов.\n" +
                "/finish — завершить сбор и отправить архив.\n" +
                "/total — показать общую сумму чеков.\n";
        }
        else
        {
            message +=
                "Отправьте фото или документы для отчётов.\n" +
                "Отправьте сумму чека текстом.\n";
        }

        message += "/info — показать информацию по вашим отчётам.\n" +
                  "/delete — удалить регистрацию (если данные введены некорректно).\n" +
                  "/clear — очистить папку от всех файлов (если что-то отправлено ошибочно).\n";

        await bot.SendTextMessageAsync(chatId, message);
    }

    private static async Task HandleCheckAmount(long chatId, string messageText, UserInfo user)
    {
        // Проверяем, активен ли сбор отчетов для города пользователя
        var cityMonthKey = activeCollections.Keys.FirstOrDefault(key => key.StartsWith(user.City));
        if (cityMonthKey == null)
        {
            await bot.SendTextMessageAsync(chatId, "Сбор отчетов не активен. Дождитесь запуска сбора администратором." +
                "Если вы не успели отправить чеки - свяжитесь с отвественным за сбор в вашем городе для решения вопроса");
            return;
        }

        // Пытаемся распарсить сумму чека
        if (!decimal.TryParse(messageText, NumberStyles.Any, new CultureInfo("ru-RU"), out var amount))
        {
            await bot.SendTextMessageAsync(chatId, "Некорректная сумма. Введите число. Формат: Рублей, копеек - через запятую, например: 18,03");
            return;
        }

        // Создаем путь к файлу manual_checks.txt
        var userFolder = $"{user.LastName}_{user.FirstName}";
        var manualChecksFilePath = Path.Combine(reportsDir, user.City, activeCollections[cityMonthKey], userFolder, "manual_checks.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(manualChecksFilePath)); // Создаем папку, если её нет

        // Записываем сумму чека в файл
        var checkEntry = $"{amount} {DateTime.Now:yyyy-MM-dd}";
        await File.AppendAllLinesAsync(manualChecksFilePath, new[] { checkEntry });

        await bot.SendTextMessageAsync(chatId, $"Сумма чека {amount} успешно добавлена.");
    }

    private static async Task HandleInfoCommand(long chatId, UserInfo user)
    {
        // Проверяем, активен ли сбор отчетов для города пользователя
        var cityMonthKey = activeCollections.Keys.FirstOrDefault(key => key.StartsWith(user.City));
        if (cityMonthKey == null)
        {
            await bot.SendTextMessageAsync(chatId, "Сбор отчетов не активен. Дождитесь запуска сбора администратором." +
            "Если вы не успели отправить чеки - свяжитесь с отвественным за сбор в вашем городе для решения вопроса");
            return;
        }

        // Создаем путь к папке пользователя
        var userFolder = $"{user.LastName}_{user.FirstName}";
        var userFolderPath = Path.Combine(reportsDir, user.City, activeCollections[cityMonthKey], userFolder);

        if (!Directory.Exists(userFolderPath))
        {
            await bot.SendTextMessageAsync(chatId, "Ваша папка не найдена.");
            return;
        }

        // Считаем количество изображений (всех допустимых форматов)
        var imageFiles = Directory.GetFiles(userFolderPath)
            .Where(file => SupportedImageFormats.Contains(Path.GetExtension(file).ToLowerInvariant()))
            .ToList();
        var imageCount = imageFiles.Count;

        // Читаем все суммы из файла auto_checks.txt
        var autoChecksFilePath = Path.Combine(userFolderPath, "auto_checks.txt");
        decimal totalAutoAmount = 0;

        if (File.Exists(autoChecksFilePath))
        {
            var autoChecks = await File.ReadAllLinesAsync(autoChecksFilePath);
            totalAutoAmount = autoChecks
                .Select(line => decimal.Parse(line.Split(' ')[0], new CultureInfo("ru-RU"))) // Берем только сумму
                .Sum();
        }

        // Читаем последнюю сумму из файла manual_checks.txt
        var manualChecksFilePath = Path.Combine(userFolderPath, "manual_checks.txt");
        string lastManualAmount = "не найдено";

        if (File.Exists(manualChecksFilePath))
        {
            var manualChecks = await File.ReadAllLinesAsync(manualChecksFilePath);
            if (manualChecks.Length > 0)
            {
                var lastManualCheck = manualChecks.Last();
                lastManualAmount = lastManualCheck.Split(' ')[0]; // Берем только сумму
            }
        }

        // Формируем сообщение
        var message = $"Информация по вашим отчётам:\n" +
                      $"Количество изображений: {imageCount}\n" +
                      $"Сумма всех автоматически извлеченных чеков: {totalAutoAmount}\n" +
                      $"Последняя введенная вручную сумма чека: {lastManualAmount}";

        await bot.SendTextMessageAsync(chatId, message);
    }

    private static async Task HandleTotalCommand(long chatId, UserInfo user)
    {
        if (!user.IsAdmin)
        {
            await bot.SendTextMessageAsync(chatId, "У вас нет прав для выполнения этой команды.");
            return;
        }

        // Проверяем, активен ли сбор отчетов для города пользователя
        var cityMonthKey = activeCollections.Keys.FirstOrDefault(key => key.StartsWith(user.City));
        if (cityMonthKey == null)
        {
            await bot.SendTextMessageAsync(chatId, "Сбор отчетов не активен. Дождитесь запуска сбора администратором." +
                "Если вы не успели отправить чеки - свяжитесь с отвественным за сбор в вашем городе для решения вопроса");
            return;
        }

        // Получаем всех пользователей из города
        var users = LoadUsers().Where(u => u.City == user.City).ToList();
        decimal totalAmount = 0;

        foreach (var u in users)
        {
            var userFolder = $"{u.LastName}_{u.FirstName}";
            var userFolderPath = Path.Combine(reportsDir, user.City, activeCollections[cityMonthKey], userFolder);

            if (!Directory.Exists(userFolderPath))
            {
                continue;
            }

            // Проверяем, есть ли ручная сумма
            var manualChecksFilePath = Path.Combine(userFolderPath, "manual_checks.txt");
            if (File.Exists(manualChecksFilePath))
            {
                var manualChecks = await File.ReadAllLinesAsync(manualChecksFilePath);
                if (manualChecks.Length > 0)
                {
                    var lastManualCheck = manualChecks.Last();
                    totalAmount += decimal.Parse(lastManualCheck.Split(' ')[0], new CultureInfo("ru-RU")); // Берем последнюю ручную сумму
                    continue;
                }
            }

            // Если ручной суммы нет, берем сумму из auto_checks.txt
            var autoChecksFilePath = Path.Combine(userFolderPath, "auto_checks.txt");
            if (File.Exists(autoChecksFilePath))
            {
                var autoChecks = await File.ReadAllLinesAsync(autoChecksFilePath);
                totalAmount += autoChecks
                    .Select(line => decimal.Parse(line.Split(' ')[0], new CultureInfo("ru-RU"))) // Берем только сумму
                    .Sum();
            }
        }

        await bot.SendTextMessageAsync(chatId, $"Общая сумма чеков: {totalAmount}");
    }
    // Словарь для хранения медиа-групп с отметкой времени первого сообщения
    private static Dictionary<string, (List<Message> Messages, DateTime LastReceived, bool IsProcessing)> mediaGroups = new();



    // Таймер для обработки завершённых медиа-групп
    private static Timer mediaGroupTimer = new Timer(async _ => await ProcessMediaGroups(), null, 2000, 2000);

    private static async Task ProcessMediaGroups()
    {
        var now = DateTime.UtcNow;
        await mediaGroupLock.WaitAsync();
        try
        {

            var expiredGroups = mediaGroups
                .Where(kvp => (now - kvp.Value.LastReceived).TotalSeconds >= 5 && !kvp.Value.IsProcessing)
                .ToList();

            foreach (var (groupId, groupInfo) in expiredGroups)
            {
                var (messages, lastReceived, _) = groupInfo;

                mediaGroups[groupId] = (messages, lastReceived, true);

                try
                {
                    var chatId = messages.First().Chat.Id;
                    var users = LoadUsers();
                    var user = users.FirstOrDefault(u => u.ChatId == chatId);
                    if (user == null) continue;

                    var cityMonthKey = activeCollections.Keys.FirstOrDefault(key => key.StartsWith(user.City));
                    if (cityMonthKey == null) continue;

                    var results = new List<string>();
                    int index = 1;

                    foreach (var photoMessage in messages)
                    {
                        var photoSize = photoMessage.Photo?.LastOrDefault();
                        if (photoSize == null) continue;

                        var fileId = photoSize.FileId;
                        var fileInfo = await bot.GetFileAsync(fileId);
                        var filePath = fileInfo.FilePath;

                        if (string.IsNullOrEmpty(filePath)) continue;

                        var userFolder = $"{user.LastName}_{user.FirstName}";
                        var destinationFilePath = Path.Combine(reportsDir, user.City, activeCollections[cityMonthKey], userFolder, $"{chatId}_{photoSize.FileUniqueId}.jpg");


                        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath));
                        if (File.Exists(destinationFilePath))
                        {
                            bot.SendMessage(chatId, "Такое изображение уже добавлено. Его копия проигнорирована.");
                            continue;
                        }
                        else
                        {
                            using (var saveImageStream = new FileStream(destinationFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                var fileUrl = $"https://api.telegram.org/file/bot{botToken}/{filePath}";
                                var httpClient = new HttpClient();
                                var imageBytes = await httpClient.GetByteArrayAsync(fileUrl);
                                await saveImageStream.WriteAsync(imageBytes, 0, imageBytes.Length);
                            }
                        }

                        var tempFilePath = Path.Combine(Path.GetTempPath(), "tess_input.png");
                        File.Copy(destinationFilePath, tempFilePath, true);

                        var extractedText = ExtractText(tempFilePath);
                        DeleteTempFile(tempFilePath);
                        var price = ExtractPrice(extractedText);

                        if (price != "Не найдено")
                        {
                            var autoChecksFilePath = Path.Combine(reportsDir, user.City, activeCollections[cityMonthKey], userFolder, "auto_checks.txt");
                            var checkEntry = $"{price} {DateTime.Now:yyyy-MM-dd}";
                            await File.AppendAllLinesAsync(autoChecksFilePath, new[] { checkEntry });
                            results.Add($"Изображение {index++}: {price} р.");
                        }
                        else
                        {
                            results.Add($"Изображение {index++}: цена не найдена");
                        }
                    }

                    await bot.SendTextMessageAsync(chatId, $"Получено {results.Count} изображений:\n" + string.Join("\n", results));
                }
                catch (Exception ex)
                {                    
                    Console.WriteLine($"Ошибка при обработке группы {groupId}: {ex.Message}");
                }
                finally
                {
                    // Удаляем полностью, повторной обработки не будет
                    mediaGroups.Remove(groupId);
                }
            }

        }
        finally { mediaGroupLock.Release(); }
    }


    private static async Task HandleNonTextMessage(long chatId, Message message)
    {
        var users = LoadUsers();
        var user = users.FirstOrDefault(u => u.ChatId == chatId);

        if (user == null)
        {
            await bot.SendTextMessageAsync(chatId, "Вы не зарегистрированы. Пожалуйста, начните с команды /start.");
            return;
        }

        // Проверяем, активен ли сбор отчетов для города пользователя
        var cityMonthKey = activeCollections.Keys.FirstOrDefault(key => key.StartsWith(user.City));
        if (cityMonthKey == null)
        {
            await bot.SendTextMessageAsync(chatId, "Сбор отчетов не активен. Дождитесь запуска сбора администратором.");
            return;
        }

        if (message.Type == MessageType.Photo)
        {
            var groupId = message.MediaGroupId ?? Guid.NewGuid().ToString(); // Для одиночных изображений создаем уникальный ID
            await mediaGroupLock.WaitAsync();
            try
            {
                if (!mediaGroups.TryGetValue(groupId, out var groupInfo))
                {
                    mediaGroups[groupId] = (new List<Message> { message }, DateTime.UtcNow, false);
                }
                else
                {
                    if (!groupInfo.IsProcessing)
                    {
                        groupInfo.Messages.Add(message);
                        mediaGroups[groupId] = (groupInfo.Messages, DateTime.UtcNow, false); // обновляем LastReceived
                    }
                    else
                    {
                        Console.WriteLine($"Группа {groupId} уже обрабатывается — изображение проигнорировано.");
                    }
                }
            }
            finally { mediaGroupLock.Release(); }
            }
        else if (message.Type == MessageType.Document)
        {
            // Обработка документов
            var document = message.Document;

            // Проверяем расширение файла
            var fileExtension = Path.GetExtension(document.FileName).ToLowerInvariant();
            if (!SupportedImageFormats.Contains(fileExtension))
            {
                await bot.SendTextMessageAsync(chatId, $"Неподдерживаемый формат файла. Ожидаются: {string.Join(", ", SupportedImageFormats)}.");
                return;
            }

            await bot.SendTextMessageAsync(chatId, "Изображение получено. Спасибо!");

            // Сохраняем документ
            var fileId = document.FileId;
            var fileInfo = await bot.GetFileAsync(fileId);
            var filePath = fileInfo.FilePath;

            if (string.IsNullOrEmpty(filePath))
            {
                await bot.SendTextMessageAsync(chatId, "Ошибка: не удалось получить путь к файлу.");
                return;
            }

            // Создаем путь с учетом фамилии и имени пользователя
            var userFolder = $"{user.LastName}_{user.FirstName}";
            var destinationFilePath = Path.Combine(reportsDir, user.City, activeCollections[cityMonthKey], userFolder, $"{chatId}_{document.FileName}");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)); // Создаем папку, если её нет

            // Скачиваем файл
            using (var saveDocumentStream = System.IO.File.Open(destinationFilePath, FileMode.Create))
            {
                var fileUrl = $"https://api.telegram.org/file/bot{botToken}/{filePath}";
                Console.WriteLine($"Попытка скачать файл по URL: {fileUrl}");

                using (var httpClient = new HttpClient())
                {
                    try
                    {
                        var documentBytes = await httpClient.GetByteArrayAsync(fileUrl);
                        await saveDocumentStream.WriteAsync(documentBytes, 0, documentBytes.Length);
                        Console.WriteLine($"Файл успешно сохранен: {destinationFilePath}");
                        saveDocumentStream.Close();
                        // Создаём папку, если её нет
                        string tempDir = Path.GetTempPath(); ;
                        Directory.CreateDirectory(tempDir);

                        // Генерируем новый путь в temp-папке
                        string tempFilePath = Path.Combine(tempDir, Path.GetFileName(destinationFilePath));

                        // Копируем файл во временную папку
                        File.Copy(destinationFilePath, tempFilePath, true);
                        Console.WriteLine($"Файл скопирован в {tempFilePath}");

                        // Извлекаем текст из изображения
                        var extractedText = ExtractText(destinationFilePath);
                        var price = ExtractPrice(extractedText);

                        if (price != "Не найдено")
                        {
                            // Записываем сумму чека в файл auto_checks.txt
                            var autoChecksFilePath = Path.Combine(reportsDir, user.City, activeCollections[cityMonthKey], userFolder, "auto_checks.txt");
                            var checkEntry = $"{price} {DateTime.Now:yyyy-MM-dd}";
                            await File.AppendAllLinesAsync(autoChecksFilePath, new[] { checkEntry });

                            await bot.SendTextMessageAsync(chatId, $"Сумма чека {price} успешно добавлена.");
                        }
                        else
                        {
                            await bot.SendTextMessageAsync(chatId, "Не удалось извлечь стоимость поездки из изображения.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка при скачивании документа: {ex.Message}");
                        await bot.SendTextMessageAsync(chatId, "Ошибка при сохранении документа.");
                    }
                }
            }
        }
        else
        {
            // Обработка других типов сообщений
            await bot.SendTextMessageAsync(chatId, "Извините, я пока не умею обрабатывать этот тип сообщений.");
        }
    }

    private static async Task HandleGodModeCommand(long chatId, UserInfo user, List<UserInfo> users)
    {
        // Проверяем, является ли пользователь администратором
        if (user.IsAdmin)
        {
            user.IsAdmin = false;
            await bot.SendTextMessageAsync(chatId, "Режим администратора отключен. Теперь вы обычный пользователь.");
        }
        else
        {
            user.IsAdmin = true;
            await bot.SendTextMessageAsync(chatId, "Режим администратора включен. Теперь вы администратор.");
        }

        // Сохраняем изменения в списке пользователей
        await SaveUsers(users);
    }
    private static async Task HandleUserRegistration(long chatId, Message message, List<UserInfo> users)
    {
        if (!pendingUserInfo.ContainsKey(chatId)) return;
        var city = "";

        if (pendingUserInfo[chatId] == "city")
        {
            // Проверяем, что message.Text не null и не пустой
            if (string.IsNullOrEmpty(message.Text))
            {
                await bot.SendTextMessageAsync(chatId, "Пожалуйста, введите текст.");
                return;
            }

            var cities = new List<string> { "Минск", "Гомель", "Новосибирск", "Кишенев", "Кишинёв", "Ереван" };
            if (!cities.Contains(message.Text, StringComparer.OrdinalIgnoreCase))
            {
                await bot.SendTextMessageAsync(chatId, "Некорректный город. Выберите из списка: Минск, Гомель, Новосибирск, Кишенев/Кишинёв, Ереван.");
                return;
            }

            // Нормализация названия города
            city = message.Text.Replace("ё", "е"); // Приводим "Кишинёв" к "Кишенев"
            pendingUserInfo[chatId] = city; // Сохраняем город во временное хранилище
            await bot.SendTextMessageAsync(chatId, "Введите вашу фамилию и имя (через пробел):");
            return;
        }

        // Получаем сохранённый город
        city = pendingUserInfo[chatId];

        // Проверяем, есть ли пользователь в списке
        var existingUser = users.FirstOrDefault(u => u.ChatId == chatId);
        if (existingUser != null)
        {
            existingUser.City = city; // Обновляем город у существующего пользователя
        }
        else
        {
            // Проверяем, что message.Text не null и не пустой
            if (string.IsNullOrEmpty(message.Text))
            {
                await bot.SendTextMessageAsync(chatId, "Пожалуйста, введите текст.");
                return;
            }

            var nameParts = message.Text.Split(' ');
            if (nameParts.Length < 2)
            {
                await bot.SendTextMessageAsync(chatId, "Введите фамилию и имя через пробел.");
                return;
            }

            // Используем username, если он есть, иначе — chatId
            var username = message.From.Username ?? chatId.ToString();
            var newUser = new UserInfo(username, chatId, nameParts[1], nameParts[0], city, false);
            users.Add(newUser);
        }

        // Сохраняем обновлённый список пользователей
        await SaveUsers(users);

        // Удаляем чат из списка ожидающих ввода
        pendingUserInfo.Remove(chatId);

        // Проверяем, активен ли сбор для города пользователя
        var cityMonthKey = activeCollections.Keys.FirstOrDefault(key => key.StartsWith(city));
        if (cityMonthKey != null)
        {
            await bot.SendTextMessageAsync(chatId, "Вы успешно зарегистрированы! Теперь отправьте отчёты. Так же отправляйте сумму по вашим чекам." +
                " Рубли и копейки разделяются запятой. \n Пример: 12,03\n" +
                "Доступные команды вы можете узнать, написав /start.");
        }
        else
        {
            await bot.SendTextMessageAsync(chatId, "Вы успешно зарегистрированы! Сбор отчетов для вашего города пока не активен. " +
                "Когда администратор запустит сбор, вам придёт уведомление.\n" +
                "Доступные команды вы можете узнать, написав /start.");
        }
    }

    private static async Task HandleCollectCommand(long chatId, string message, UserInfo user)
    {
        var parts = message.Split(' ');
        if (parts.Length < 3)
        {
            await bot.SendTextMessageAsync(chatId, "Используйте: /collect <Месяц> <Дата завершения>. \nПример: /collect февраль 2025.03.20");
            return;
        }

        var month = parts[1];
        var endDate = parts[2];

        if (!DateTime.TryParse(endDate, out var date))
        {
            await bot.SendTextMessageAsync(chatId, "Некорректная дата завершения.");
            return;
        }

        var cityMonthKey = $"{user.City}-{month}";
        if (activeCollections.ContainsKey(cityMonthKey))
        {
            await bot.SendTextMessageAsync(chatId, $"Сбор за {month} в {user.City} уже запущен.");
            return;
        }

        activeCollections[cityMonthKey] = month;
        await SaveActiveCollections(); // Сохраняем активный сбор в файл
        await NotifyUsers(user.City, month, endDate);
        await bot.SendTextMessageAsync(chatId, $"Сбор отчётов за {month} для {user.City} запущен до {endDate}.");
    }

    private static async Task HandleFinishCommand(long chatId, UserInfo user)
    {
        var cityMonthKey = activeCollections.Keys.FirstOrDefault(key => key.StartsWith(user.City));
        if (cityMonthKey != null)
        {
            var archivePath = CreateArchive(user.City, activeCollections[cityMonthKey]);
            if (string.IsNullOrEmpty(archivePath))
            {
                await bot.SendTextMessageAsync(chatId, "Ошибка при создании архива.");
                return;
            }

            // Формируем текстовый отчет
            var reportText = GenerateReport(user.City, activeCollections[cityMonthKey]);

            // Отправляем архив
            using (var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read))
            {
                await bot.SendDocumentAsync(chatId, new InputFileStream(fileStream, Path.GetFileName(archivePath)));
            }

            // Отправляем текстовый отчет
            await SendLongMessage(reportText, chatId);

            // Удаляем архив после отправки
            File.Delete(archivePath);

            // Рассылаем уведомления пользователям о завершении сбора
            await NotifyUsersAboutCollectionEnd(user.City, activeCollections[cityMonthKey]);

            // Удаляем активный сбор из файла
            await RemoveActiveCollection(cityMonthKey);

            // Удаляем активный сбор из памяти
            activeCollections.Remove(cityMonthKey);
        }
        else
        {
            await bot.SendTextMessageAsync(chatId, "Сбор отчётов ещё не начат.");
        }
    }

    private static async Task NotifyUsersAboutCollectionEnd(string city, string month)
    {
        var users = LoadUsers().Where(u => u.City == city && !u.IsAdmin);
        foreach (var user in users)
        {
            await bot.SendTextMessageAsync(user.ChatId, $"Сбор отчётов за {month} завершён. Спасибо за участие!");
        }
    }
    private static async Task SendLongMessage(string text, long chatId)
    {
        const int MaxLength = 4000;

        for (int i = 0; i < text.Length; i += MaxLength)
        {
            var part = text.Substring(i, Math.Min(MaxLength, text.Length - i));
            await bot.SendTextMessageAsync(chatId, part);
        }
    }


    private static string GenerateReport(string city, string month)
    {
        var dirPath = Path.Combine(reportsDir, city, month);
        var reportText = new StringBuilder();

        if (!Directory.Exists(dirPath))
        {
            return "Папка с отчетами не найдена.";
        }

        // Получаем все папки пользователей
        var userFolders = Directory.GetDirectories(dirPath);

        foreach (var userFolder in userFolders)
        {
            var userFolderName = Path.GetFileName(userFolder);
            var userFolderPath = Path.Combine(dirPath, userFolderName);

            // Получаем количество изображений
            var imageFiles = Directory.GetFiles(userFolderPath)
                .Where(file => SupportedImageFormats.Contains(Path.GetExtension(file).ToLowerInvariant()))
                .ToList();
            var imageCount = imageFiles.Count;

            // Получаем сумму из auto_checks.txt
            var autoChecksFilePath = Path.Combine(userFolderPath, "auto_checks.txt");
            decimal totalAutoAmount = 0;

            if (File.Exists(autoChecksFilePath))
            {
                var autoChecks = File.ReadAllLines(autoChecksFilePath);
                totalAutoAmount = autoChecks
                    .Select(line =>
                    {
                        var amountPart = line.Split(' ')[0];
                        // Заменяем точку на запятую, если нужно
                        amountPart = amountPart.Replace('.', ',');
                        return decimal.Parse(amountPart);
                    })
                    .Sum();
            }

            // Получаем последнюю сумму из manual_checks.txt
            var manualChecksFilePath = Path.Combine(userFolderPath, "manual_checks.txt");
            string lastManualAmount = "не найдено";

            if (File.Exists(manualChecksFilePath))
            {
                var manualChecks = File.ReadAllLines(manualChecksFilePath);
                if (manualChecks.Length > 0)
                {
                    var lastManualCheck = manualChecks.Last();
                    lastManualAmount = lastManualCheck.Split(' ')[0]; // Берем только сумму
                }
            }

            // Добавляем информацию в отчет
            reportText.AppendLine($"{userFolderName}");
            reportText.AppendLine($"Количество скриншотов: {imageCount}");
            reportText.AppendLine($"Сумма авточеков: {totalAutoAmount}");
            reportText.AppendLine($"Последнее значение введенное руками: {lastManualAmount}");
            reportText.AppendLine();
        }

        return reportText.ToString();
    }

    private static async Task NotifyUsers(string city, string month, string endDate)
    {
        var users = LoadUsers().Where(u => u.City == city && !u.IsAdmin);
        foreach (var user in users)
        {
            await bot.SendTextMessageAsync(user.ChatId, $"Сбор отчётов за {month} начат до {endDate}. Отправьте ваши скриншоты. Так же можете отправьте сумму по вашим отчётам текстом.\n" +
                "Доступные команды вы можете узнать, написав /start." +
                "Сумма с изображений считывается автоматически, в случае ошибки необходимо самостоятельно ввести сумму за все поездки");
        }
    }

    private static string CreateArchive(string city, string month)
    {
        var dirPath = Path.Combine(reportsDir, city, month);
        var zipPath = Path.Combine(reportsDir, city, $"{month}.zip");

        if (!Directory.Exists(dirPath))
        {
            Console.WriteLine($"Папка {dirPath} не существует.");
            return string.Empty;
        }

        try
        {
            // Убедимся, что папка не пуста
            if (!Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories).Any())
            {
                Console.WriteLine($"Папка {dirPath} пуста.");
                return string.Empty;
            }

            // Создаем архив
            ZipFile.CreateFromDirectory(dirPath, zipPath);
            Console.WriteLine($"Архив создан: {zipPath}");
            return zipPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при создании архива: {ex.Message}");
            return string.Empty;
        }
    }

    private static async Task SaveUsers(List<UserInfo> users)
    {
        await fileLock.WaitAsync();
        try
        {
            var lines = users.Select(u => u.ToString());
            await File.WriteAllLinesAsync(usersFile, lines);
        }
        finally
        {
            fileLock.Release();
        }
    }

    private static List<UserInfo> LoadUsers()
    {
        if (!File.Exists(usersFile)) return new List<UserInfo>();

        var users = new List<UserInfo>();
        foreach (var line in File.ReadAllLines(usersFile))
        {
            try
            {
                users.Add(UserInfo.Parse(line));
            }
            catch (FormatException ex)
            {
                Console.WriteLine($"Ошибка при разборе строки: {line}. {ex.Message}");
            }
        }
        return users;
    }

    private static async Task HandleDeleteCommand(long chatId, List<UserInfo> users)
    {
        var user = users.FirstOrDefault(u => u.ChatId == chatId);
        if (user == null)
        {
            await bot.SendTextMessageAsync(chatId, "Вы не зарегистрированы.");
            return;
        }

        // Удаляем пользователя из списка
        users.Remove(user);
        await SaveUsers(users);

        await bot.SendTextMessageAsync(chatId, "Ваша регистрация удалена. Теперь вы можете зарегистрироваться заново.");
    }

    private static async Task HandleClearCommand(long chatId, UserInfo user)
    {
        // Проверяем, активен ли сбор отчетов для города пользователя
        var cityMonthKey = activeCollections.Keys.FirstOrDefault(key => key.StartsWith(user.City));
        if (cityMonthKey == null)
        {
            await bot.SendTextMessageAsync(chatId, "Сбор отчетов не активен. Дождитесь запуска сбора администратором.");
            return;
        }

        // Создаем путь к папке пользователя
        var userFolder = $"{user.LastName}_{user.FirstName}";
        var userFolderPath = Path.Combine(reportsDir, user.City, activeCollections[cityMonthKey], userFolder);

        if (!Directory.Exists(userFolderPath))
        {
            await bot.SendTextMessageAsync(chatId, "Ваша папка не найдена.");
            return;
        }

        // Удаляем все файлы в папке пользователя
        foreach (var file in Directory.GetFiles(userFolderPath))
        {
            File.Delete(file);
        }

        await bot.SendTextMessageAsync(chatId, "Ваша папка очищена. Теперь вы можете начать заново.");
    }

    private static Task ErrorHandler(ITelegramBotClient botClient, Exception exception, CancellationToken token)
    {
        Console.WriteLine($"Ошибка: {exception.Message}");
        return Task.CompletedTask;
    }
}

public class UserInfo
{
    public string Username { get; set; } // Может быть null
    public long ChatId { get; set; } // Уникальный идентификатор
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string City { get; set; }
    public bool IsAdmin { get; set; }

    public UserInfo(string username, long chatId, string firstName, string lastName, string city, bool isAdmin)
    {
        Username = username;
        ChatId = chatId;
        FirstName = firstName;
        LastName = lastName;
        City = city;
        IsAdmin = isAdmin;
    }

    public override string ToString()
    {
        // Сохраняем username, если он есть, иначе оставляем пустое место
        var username = Username ?? "null";
        return $"{username} {ChatId} {FirstName} {LastName} {City} {IsAdmin}";
    }

    public static UserInfo Parse(string line)
    {
        var parts = line.Split(' ');
        if (parts.Length < 5 || parts.Length > 6)
        {
            throw new FormatException("Некорректный формат строки. Ожидается 5 или 6 значений, разделенных пробелами.");
        }

        // Если строка содержит 5 значений, это старый формат (без username)
        if (parts.Length == 5)
        {
            return new UserInfo(
                null, // username отсутствует
                long.Parse(parts[0]), // ChatId
                parts[1], // FirstName
                parts[2], // LastName
                parts[3], // City
                bool.Parse(parts[4]) // IsAdmin
            );
        }

        // Если строка содержит 6 значений, это новый формат (с username)
        return new UserInfo(
            parts[0], // Username
            long.Parse(parts[1]), // ChatId
            parts[2], // FirstName
            parts[3], // LastName
            parts[4], // City
            bool.Parse(parts[5]) // IsAdmin
        );
    }
}