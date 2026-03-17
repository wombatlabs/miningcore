using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Bitcoin.DaemonResponses
{
    public class Developer
    {
        [JsonProperty("payee")]
        public string Payee { get; set; }

        [JsonProperty("script")]
        public string Script { get; set; }

        [JsonProperty("amount")]
        public long Amount { get; set; }
    }

    public class DeveloperBlockTemplateExtra
    {
        // Accepts either a single object or an array and normalizes to a list.
        [JsonProperty("developer")]
        [JsonConverter(typeof(DeveloperListOrObjectConverter))]
        public List<Developer> Developer { get; set; } = new List<Developer>();

        [JsonProperty("developer_payments_started")]
        public bool DeveloperPaymentsStarted { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();
    }

    /// <summary>
    /// Allows "developer" to be either an array of Developer or a single Developer object.
    /// </summary>
    public sealed class DeveloperListOrObjectConverter : JsonConverter
    {
        public override bool CanConvert(System.Type objectType) =>
            objectType == typeof(List<Developer>);

        public override object ReadJson(JsonReader reader, System.Type objectType, object existingValue, JsonSerializer serializer)
        {
            var token = JToken.Load(reader);
            var list = new List<Developer>();

            if (token.Type == JTokenType.Array)
            {
                foreach (var item in token)
                {
                    var entry = item.ToObject<Developer>(serializer);
                    if (entry != null) list.Add(entry);
                }
            }
            else if (token.Type == JTokenType.Object)
            {
                var entry = token.ToObject<Developer>(serializer);
                if (entry != null) list.Add(entry);
            }

            return list;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            // Serialize as array consistently
            serializer.Serialize(writer, value);
        }
    }
}
