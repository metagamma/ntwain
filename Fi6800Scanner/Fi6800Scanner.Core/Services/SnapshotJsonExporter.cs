using System.IO;
using Fi6800Scanner.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Fi6800Scanner.Core.Services
{
    public static class SnapshotJsonExporter
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = { new StringEnumConverter() },
            NullValueHandling = NullValueHandling.Include
        };

        public static string ToJson(Fi6800CapabilitySnapshot snap) =>
            JsonConvert.SerializeObject(snap, Settings);

        public static void Save(Fi6800CapabilitySnapshot snap, string path)
        {
            File.WriteAllText(path, ToJson(snap));
        }

        public static Fi6800CapabilitySnapshot Load(string path) =>
            JsonConvert.DeserializeObject<Fi6800CapabilitySnapshot>(File.ReadAllText(path), Settings);
    }
}
