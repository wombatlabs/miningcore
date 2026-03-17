using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Bitcoin.DaemonResponses
{
    public class Community
    {
        [JsonProperty("payee")]
        public string Payee { get; set; }

        [JsonProperty("script")]
        public string Script { get; set; }

        [JsonProperty("amount")]
        public long Amount { get; set; }
    }

    public class CommunityBlockTemplateExtra
    {
        [JsonProperty("community")]
        [JsonConverter(typeof(CommunityListOrObjectConverter))]
        public List<Community> Community { get; set; } = new List<Community>();

        [JsonProperty("community_payments_started")]
        public bool CommunityPaymentsStarted { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; } = new Dictionary<string, JToken>();
    }

    /// <summary>
    /// Allows "community" to be either an array of Community or a single Community object.
    /// </summary>
    public sealed class CommunityListOrObjectConverter : JsonConverter
    {
        public override bool CanConvert(System.Type objectType) =>
            objectType == typeof(List<Community>);

        public override object ReadJson(JsonReader reader, System.Type objectType, object existingValue, JsonSerializer serializer)
        {
            var token = JToken.Load(reader);

            var list = new List<Community>();

            if (token.Type == JTokenType.Array)
            {
                foreach (var item in token)
                {
                    var entry = item.ToObject<Community>(serializer);
                    if (entry != null) list.Add(entry);
                }
            }
            else if (token.Type == JTokenType.Object)
            {
                var entry = token.ToObject<Community>(serializer);
                if (entry != null) list.Add(entry);
            }

            return list;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            // Always serialize as array (safe and consistent)
            serializer.Serialize(writer, value);
        }
    }
}
