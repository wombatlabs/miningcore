// Kaspa/RPC/ExternalWalletHttpClient.cs
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Miningcore.Wallets
{
    public class ExternalWalletHttpClient
    {
        private readonly HttpClient http;

        public ExternalWalletHttpClient(string baseUrl)
        {
            if(string.IsNullOrEmpty(baseUrl))
                throw new ArgumentNullException(nameof(baseUrl));

            http = new HttpClient
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(20)
            };
        }

        private static StringContent JsonBody(object o) =>
            new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

        public async Task<string> GetVersionAsync()
        {
            var res = await http.GetAsync("/version");
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync();
        }

        public async Task<JsonDocument> GetAddressAsync()
        {
            var res = await http.GetAsync("/address");
            res.EnsureSuccessStatusCode();
            return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        }

        public async Task<JsonDocument> ListAsync()
        {
            var res = await http.GetAsync("/list");
            res.EnsureSuccessStatusCode();
            return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        }

        public async Task<JsonDocument> DetailsAsync()
        {
            var res = await http.GetAsync("/details");
            res.EnsureSuccessStatusCode();
            return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        }

        public async Task<JsonDocument> EstimateAsync(string amount)
        {
            var res = await http.GetAsync($"/estimate?amount={Uri.EscapeDataString(amount)}");
            res.EnsureSuccessStatusCode();
            return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        }

        public async Task<JsonDocument> SendAsync(string to, string amount)
        {
            var res = await http.PostAsync("/send", JsonBody(new { to, amount }));
            res.EnsureSuccessStatusCode();
            return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        }

        public async Task<JsonDocument> RpcAsync(string method, object parameters = null)
        {
            var res = await http.PostAsync("/rpc", JsonBody(new { method, @params = parameters }));
            res.EnsureSuccessStatusCode();
            return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
        }
    }
}
