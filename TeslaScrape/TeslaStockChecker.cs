using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace TeslaScrape
{
    public class TeslaStockChecker
    {
        private readonly HttpClient _httpClient = new();
        private const string TeslaApiUrl = "https://www.tesla.com/coinorder/api/v4/inventory-results?query=%7B%22query%22%3A%7B%22model%22%3A%22my%22%2C%22condition%22%3A%22new%22%2C%22options%22%3A%7B%7D%2C%22arrangeby%22%3A%22Price%22%2C%22order%22%3A%22asc%22%2C%22market%22%3A%22TR%22%2C%22language%22%3A%22tr%22%2C%22super_region%22%3A%22north%20america%22%2C%22lng%22%3A%22%22%2C%22lat%22%3A%22%22%2C%22zip%22%3A%22%22%2C%22range%22%3A0%7D%2C%22offset%22%3A0%2C%22count%22%3A24%2C%22outsideOffset%22%3A0%2C%22outsideSearch%22%3Afalse%2C%22isFalconDeliverySelectionEnabled%22%3Atrue%2C%22version%22%3A%22v2%22%7D"; // senin API URL
        private const string TeslaWebUrl = "https://www.tesla.com/tr_TR/inventory/new/my?fbclid=PAQ0xDSwKtXb1leHRuA2FlbQIxMAABp-1IFv_8q7wtJznZDm-81Fq2S6M40xACGnnUPSOg74rBqME0JUsASOBndTbx_aem_FgXfG1mzOVwirWhe4tu8Vg";

        public async Task CheckTeslaStockAsync()
        {
            var chromeOptions = new ChromeOptions();
            //chromeOptions.AddArgument("--headless=new");
            chromeOptions.AddArgument("--disable-gpu");
            chromeOptions.AddArgument("--no-sandbox");

            using var driver = new ChromeDriver(chromeOptions);

            try
            {
                driver.Navigate().GoToUrl(TeslaApiUrl);
                await Task.Delay(10000); // sayfanın tam yüklenmesi için bekle

                // JSON içeriği API URL'sinden direkt alıyoruz
                // Selenium sayfa olarak JSON'u text şeklinde açar.
                var pageSource = driver.PageSource;

                // pageSource HTML gibi görünebilir, ama genelde JSON stringi oluyor
                // Genelde <pre> tag içinde JSON olabilir
                // O yüzden <pre> tag içeriğini alalım

                var preElement = driver.FindElement(By.TagName("pre"));
                var jsonText = preElement.Text;

                var jsonDoc = JsonDocument.Parse(jsonText);

                if (jsonDoc.RootElement.TryGetProperty("total_matches_found", out var totalMatches))
                {
                    int count = totalMatches.GetInt32();
                    if (count > 0)
                        Console.WriteLine($"📢 STOK VAR! Toplam {count} adet bulundu.");
                    else
                        Console.WriteLine("Stok yok.");
                }
                else
                {
                    Console.WriteLine("total_matches_found bilgisi bulunamadı.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Hata: {ex.Message}");
            }
            finally
            {
                driver.Quit();
            }
        }
    }
}
