// TeslaWorkerService.cs
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using TeslaScrape;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly HttpClient _httpClient;
    private readonly TeslaWatcherSettings _settings;
    private readonly TelegramBotClient _telegramBot;

    private DateTime _lastStockFoundTime = DateTime.MinValue;
    private int _lastStockCount = 0;

    public Worker(
        ILogger<Worker> logger,
        HttpClient httpClient,
        IOptions<TeslaWatcherSettings> settings)
    {
        _logger = logger;
        _httpClient = httpClient;
        _settings = settings.Value;
        _telegramBot = new TelegramBotClient(_settings.TelegramBotToken);
    }



    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🚗 Tesla Model Y Stok İzleyici Başlatılıyor...");

        // Bot connection test
        try
        {
            var me = await _telegramBot.GetMeAsync(cancellationToken);
            _logger.LogInformation("✅ Telegram Bot bağlantısı başarılı: @{username}", me.Username);

            // Başlangıç mesajı gönder
            var startMessage = "🤖 <b>Tesla Model Y Stok İzleyici Aktif</b>\n\n" +
                              $"⏰ Kontrol Aralığı: {_settings.CheckIntervalMinutes} dakika\n" +
                              $"📅 Başlangıç: {DateTime.Now:dd.MM.yyyy HH:mm}\n\n" +
                              "🔍 Stok aranmaya başlandı...";

            await _telegramBot.SendTextMessageAsync(
                chatId: _settings.TelegramChatId,
                text: startMessage,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Telegram Bot bağlantı hatası");
            throw;
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🔄 Tesla stok kontrol döngüsü başlatıldı");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckTeslaStock(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Stok kontrol hatası");

                // Hata durumunda Telegram bilgilendirmesi (opsiyonel)
                if (ShouldNotifyError(ex))
                {
                    await SendErrorNotification(ex, stoppingToken);
                }
            }

            var delayMinutes = _settings.CheckIntervalMinutes;
            _logger.LogInformation("⏰ Sonraki kontrol {delay} dakika sonra...", delayMinutes);

            await Task.Delay(TimeSpan.FromMinutes(delayMinutes), stoppingToken);
        }
    }

    private async Task CheckTeslaStock(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🔍 Tesla Model Y stok kontrol ediliyor...");

        try
        {
            // Timeout ekle
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            // Anti-bot headers - her istekte rastgele User-Agent
            var userAgents = new[]
            {
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36",
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:120.0) Gecko/20100101 Firefox/120.0"
            };

            var random = new Random();
            var selectedUserAgent = userAgents[random.Next(userAgents.Length)];

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", selectedUserAgent);
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
            _httpClient.DefaultRequestHeaders.Add("Accept-Language", "tr-TR,tr;q=0.9,en;q=0.8");
            _httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br"); // GERİ EKLENDİ
            _httpClient.DefaultRequestHeaders.Add("Referer", "https://www.tesla.com/tr_TR/inventory/new/my");
            _httpClient.DefaultRequestHeaders.Add("Origin", "https://www.tesla.com");
            _httpClient.DefaultRequestHeaders.Add("DNT", "1");
            _httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
            _httpClient.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
            _httpClient.DefaultRequestHeaders.Add("Cache-Control", "no-cache");
            _httpClient.DefaultRequestHeaders.Add("Pragma", "no-cache");

            // Fake IP ekle
            var fakeIp = $"{random.Next(1, 255)}.{random.Next(1, 255)}.{random.Next(1, 255)}.{random.Next(1, 255)}";
            _httpClient.DefaultRequestHeaders.Add("X-Forwarded-For", fakeIp);
            _httpClient.DefaultRequestHeaders.Add("X-Real-IP", fakeIp);

            _logger.LogInformation("🌐 API isteği gönderiliyor... (Timeout: 30s)");

            var response = await _httpClient.GetAsync(_settings.TeslaApiUrl, combinedCts.Token);

            _logger.LogInformation("📡 API yanıtı alındı: {statusCode}", response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                var jsonContent = "";

                // Content-Encoding kontrolü
                if (response.Content.Headers.ContentEncoding.Contains("gzip"))
                {
                    _logger.LogInformation("📦 GZIP içerik tespit edildi, açılıyor...");

                    // GZIP'i manuel olarak aç
                    using var gzipStream = new System.IO.Compression.GZipStream(
                        await response.Content.ReadAsStreamAsync(combinedCts.Token),
                        System.IO.Compression.CompressionMode.Decompress);

                    using var reader = new StreamReader(gzipStream);
                    jsonContent = await reader.ReadToEndAsync();
                }
                else
                {
                    // Normal içerik
                    jsonContent = await response.Content.ReadAsStringAsync(combinedCts.Token);
                }

                _logger.LogInformation("📄 JSON içerik alındı: {length} karakter", jsonContent.Length);
                _logger.LogInformation("🔍 JSON önizleme: {preview}", jsonContent.Substring(0, Math.Min(100, jsonContent.Length)));

                await ProcessStockResponse(jsonContent, cancellationToken);
            }
            else
            {
                _logger.LogWarning("⚠️ Tesla API yanıt vermedi: {statusCode} - {reason}",
                    response.StatusCode, response.ReasonPhrase);

                // 403 veya 429 ise daha uzun bekle
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                    response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning("🚫 Rate limit veya blok! 5 dakika bekleniyor...");
                    await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                }
            }
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || ex.CancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("⏰ API isteği timeout oldu (30 saniye)");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "🌐 HTTP isteği hatası");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Beklenmeyen hata");
        }
    }

    private async Task ProcessStockResponse(string jsonContent, CancellationToken cancellationToken)
    {
        try
        {
            using var jsonDoc = JsonDocument.Parse(jsonContent);
            var root = jsonDoc.RootElement;

            var totalMatches = 0;
            if (root.TryGetProperty("total_matches_found", out var totalElement))
            {
                totalMatches = totalElement.GetInt32();
            }

            _logger.LogInformation("📊 Bulunan stok: {count} adet", totalMatches);

            if (totalMatches > 0)
            {
                // Stok durumu değişti mi kontrol et
                var shouldNotify = ShouldNotifyStockFound(totalMatches);

                if (shouldNotify)
                {
                    await SendStockFoundNotification(totalMatches, jsonContent, cancellationToken);
                    _lastStockFoundTime = DateTime.Now;
                    _lastStockCount = totalMatches;
                }
            }
            else
            {
                //await SendStockFoundNotification(totalMatches, jsonContent, cancellationToken);
                //_lastStockFoundTime = DateTime.Now;
                //_lastStockCount = totalMatches;
                // Stok yok - sadece önceden stok varsa bildir
                if (_lastStockCount > 0)
                {
                    await SendStockEmptyNotification(cancellationToken);
                    _lastStockCount = 0;
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "❌ JSON parse hatası");
        }
    }

    private bool ShouldNotifyStockFound(int currentStock)
    {
        // İlk defa stok bulundu
        if (_lastStockCount == 0 && currentStock > 0)
            return true;

        // Stok sayısı önemli ölçüde değişti
        if (Math.Abs(currentStock - _lastStockCount) >= 2)
            return true;

        // Son bildirimden 30 dakika geçti ve hala stok var
        if (_lastStockFoundTime.AddMinutes(30) < DateTime.Now && currentStock > 0)
            return true;

        return false;
    }

    private async Task SendStockFoundNotification(int stockCount, string jsonContent, CancellationToken cancellationToken)
    {
        try
        {
            var vehicleDetails = ParseVehicleDetails(jsonContent);

            var message = "🚗 <b>TESLA MODEL Y STOK VAR!</b>\n\n" +
                         $"📦 Toplam Stok: <b>{stockCount}</b> adet\n" +
                         $"⏰ Tarih: {DateTime.Now:dd.MM.yyyy HH:mm}\n\n";

            if (vehicleDetails.Any())
            {
                message += "🚙 <b>Mevcut Araçlar:</b>\n\n";
                var count = 1;
                foreach (var vehicle in vehicleDetails.Take(2)) // İlk 2 araç detaylı
                {
                    message += $"<b>🚗 Araç {count}:</b>\n{vehicle}\n\n";
                    count++;
                }

                if (vehicleDetails.Count > 2)
                {
                    message += $"... ve <b>{vehicleDetails.Count - 2}</b> adet daha\n\n";
                }
            }

            message += "🔗 <a href=\"https://www.tesla.com/tr_TR/inventory/new/my\">Stokları Görüntüle</a>";

            await _telegramBot.SendTextMessageAsync(
                chatId: _settings.TelegramChatId,
                text: message,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);

            _logger.LogInformation("✅ Stok bulundu bildirimi gönderildi!");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Telegram mesaj gönderme hatası");
        }
    }

    private async Task SendStockEmptyNotification(CancellationToken cancellationToken)
    {
        try
        {
            var message = "📭 <b>Tesla Model Y Stok Tükendi</b>\n\n" +
                         $"⏰ Tarih: {DateTime.Now:dd.MM.yyyy HH:mm}\n\n" +
                         "🔍 Stok aranmaya devam ediliyor...";

            await _telegramBot.SendTextMessageAsync(
                chatId: _settings.TelegramChatId,
                text: message,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);

            _logger.LogInformation("📭 Stok tükendi bildirimi gönderildi");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Stok tükendi mesaj hatası");
        }
    }

    private List<string> ParseVehicleDetails(string jsonContent)
    {
        var vehicles = new List<string>();

        try
        {
            using var jsonDoc = JsonDocument.Parse(jsonContent);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var vehicle in resultsElement.EnumerateArray().Take(5))
                {
                    // Fiyat
                    var price = vehicle.TryGetProperty("Price", out var priceElement) ?
                        priceElement.GetInt32().ToString("N0") + " TL" : "Fiyat Belirtilmemiş";

                    // Yıl
                    var year = vehicle.TryGetProperty("Year", out var yearElement) ?
                        yearElement.GetInt32().ToString() : "2024";

                    // Renk
                    var paint = "Bilinmeyen Renk";
                    if (vehicle.TryGetProperty("PAINT", out var paintElement) && paintElement.ValueKind == JsonValueKind.Array)
                    {
                        var paintCode = paintElement.EnumerateArray().FirstOrDefault().GetString();
                        paint = GetPaintName(paintCode);
                    }

                    // İç mekan
                    var interior = "";
                    if (vehicle.TryGetProperty("INTERIOR", out var interiorElement) && interiorElement.ValueKind == JsonValueKind.Array)
                    {
                        var interiorCode = interiorElement.EnumerateArray().FirstOrDefault().GetString();
                        interior = GetInteriorName(interiorCode);
                    }

                    // Jantlar
                    var wheels = "";
                    if (vehicle.TryGetProperty("WHEELS", out var wheelElement) && wheelElement.ValueKind == JsonValueKind.Array)
                    {
                        var wheelCode = wheelElement.EnumerateArray().FirstOrDefault().GetString();
                        wheels = GetWheelName(wheelCode);
                    }

                    // Autopilot
                    var autopilot = "";
                    if (vehicle.TryGetProperty("AUTOPILOT", out var autopilotElement) && autopilotElement.ValueKind == JsonValueKind.Array)
                    {
                        var autopilotCode = autopilotElement.EnumerateArray().FirstOrDefault().GetString();
                        autopilot = GetAutopilotName(autopilotCode);
                    }

                    // Teslimat tarihi
                    var delivery = vehicle.TryGetProperty("ActualGADateRange", out var deliveryElement) ?
                        deliveryElement.GetString() : "Belirtilmemiş";

                    // VIN
                    var vin = vehicle.TryGetProperty("VIN", out var vinElement) ?
                        vinElement.GetString()?.Substring(6) : "******"; // Son 6 hane

                    // Demo araç mı?
                    var isDemo = vehicle.TryGetProperty("IsDemo", out var demoElement) && demoElement.GetBoolean();
                    var demoText = isDemo ? " (DEMO)" : "";

                    // Araç detayını oluştur
                    var vehicleInfo = $"<b>{year} Model Y{demoText}</b>\n" +
                                     $"🎨 {paint}\n" +
                                     $"🪑 {interior}\n" +
                                     $"⚙️ {wheels}\n" +
                                     $"🤖 {autopilot}\n" +
                                     $"📅 Teslimat: {delivery}\n" +
                                     $"💰 <b>{price}</b>\n" +
                                     $"🔢 VIN: ***{vin}";

                    vehicles.Add(vehicleInfo);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Araç detayları parse edilemedi");
        }

        return vehicles;
    }

  
    private string GetPaintName(string paintCode)
    {
        return paintCode?.ToUpper() switch
        {
            "PBSB" => "İnci Beyaz Metalik",
            "PMNG" => "Gece Gümüşü Metalik",
            "PPSB" => "İnci Beyaz",
            "PPMR" => "Çok Katmanlı Kırmızı",
            "PPSR" => "Gri Metalik",
            "PPBW" => "İnci Beyaz Çok Katmanlı",
            "PPSW" => "İnci Beyaz Çok Katmanlı",
            "PMTG" => "Gece Gümüşü Metalik",
            _ => paintCode ?? "Bilinmeyen Renk"
        };
    }

    private string GetInteriorName(string interiorCode)
    {
        return interiorCode?.ToUpper() switch
        {
            "IWW2" => "Beyaz İç Mekan",
            "IBW2" => "Siyah İç Mekan",
            "ICW2" => "Krem İç Mekan",
            "IBC2" => "Siyah ve Kırmızı İç Mekan",
            _ => interiorCode ?? "Standart İç Mekan"
        };
    }

    private string GetWheelName(string wheelCode)
    {
        return wheelCode?.ToUpper() switch
        {
            "WTAB" => "19'' Gemini Jantlar",
            "WT20" => "20'' İnduction Jantlar",
            "WTAE" => "21'' Überturbine Jantlar",
            "WTAS" => "19'' Apollo Jantlar",
            _ => wheelCode ?? "Standart Jantlar"
        };
    }

    private string GetAutopilotName(string autopilotCode)
    {
        return autopilotCode?.ToUpper() switch
        {
            "APFB" => "Full Self-Driving Yeteneği",
            "APBS" => "Temel Autopilot",
            "APPA" => "Gelişmiş Autopilot",
            _ => autopilotCode ?? "Temel Autopilot"
        };
    }

    private bool ShouldNotifyError(Exception ex)
    {
        // Kritik hatalar için bildirim gönder
        return ex is HttpRequestException || ex is TaskCanceledException;
    }

    private async Task SendErrorNotification(Exception ex, CancellationToken cancellationToken)
    {
        try
        {
            var message = "⚠️ <b>Tesla Bot Hatası</b>\n\n" +
                         $"🚫 Hata: {ex.GetType().Name}\n" +
                         $"⏰ Tarih: {DateTime.Now:dd.MM.yyyy HH:mm}\n\n" +
                         "🔄 Sistem çalışmaya devam ediyor...";

            await _telegramBot.SendTextMessageAsync(
                chatId: _settings.TelegramChatId,
                text: message,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }
        catch
        {
            // Hata bildiriminde hata olursa sessiz geç
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🛑 Tesla stok izleyici durduruluyor...");

        try
        {
            var stopMessage = "🛑 <b>Tesla Stok İzleyici Durdu</b>\n\n" +
                             $"⏰ Durdurulma: {DateTime.Now:dd.MM.yyyy HH:mm}";

            await _telegramBot.SendTextMessageAsync(
                chatId: _settings.TelegramChatId,
                text: stopMessage,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken);
        }
        catch
        {
            // Stop mesajında hata olursa sessiz geç
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _httpClient?.Dispose();
        //_telegramBot?.Dispose();
        base.Dispose();
    }
}