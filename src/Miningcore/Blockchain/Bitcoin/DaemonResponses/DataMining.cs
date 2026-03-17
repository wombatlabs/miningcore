using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Bitcoin.DaemonResponses
{
    public class DataMining
    {
        [JsonProperty("payee")]
        public string Payee { get; set; }

        [JsonProperty("script")]
        public string Script { get; set; }

        [JsonProperty("amount")]
        public long Amount { get; set; }
    }

    public class DataMiningBlockTemplateExtra
    {
        // Accepts either a single object or an array and normalizes to a list.
        [JsonProperty("datamining")]
        [JsonConverter(typeof(DataMiningListOrObjectConverter))]
        public List<DataMining> DataMining { get; set; } = new List<DataMining>();

        [JsonProperty("datamining_payments_started")]
        public bool DataMiningPaymentsStarted { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();
    }

    /// <summary>
    /// Allows "datamining" to be either an array of DataMining or a single DataMining object.
    /// </summary>
    public sealed class DataMiningListOrObjectConverter : JsonConverter
    {
        public override bool CanConvert(System.Type objectType) =>
            objectType == typeof(List<DataMining>);

        public override object ReadJson(JsonReader reader, System.Type objectType, object existingValue, JsonSerializer serializer)
        {
            var token = JToken.Load(reader);
            var list = new List<DataMining>();

            if (token.Type == JTokenType.Array)
            {
                foreach (var item in token)
                {
                    var entry = item.ToObject<DataMining>(serializer);
                    if (entry != null) list.Add(entry);
                }
            }
            else if (token.Type == JTokenType.Object)
            {
                var entry = token.ToObject<DataMining>(serializer);
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
