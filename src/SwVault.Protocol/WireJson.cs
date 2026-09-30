using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SwVault.Protocol
{
    /// <summary>
    /// JSON helper built on DataContractJsonSerializer so both the net48 add-in and the
    /// net10 agent can use it without any third-party package.
    /// </summary>
    public static class WireJson
    {
        private static readonly DataContractJsonSerializerSettings Settings = new DataContractJsonSerializerSettings
        {
            UseSimpleDictionaryFormat = true,
        };

        public static string Serialize<T>(T value)
        {
            if (value == null) return null;
            var serializer = new DataContractJsonSerializer(typeof(T), Settings);
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, value);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        /// <summary>Serializes using the runtime type (for payloads only known as object).</summary>
        public static string SerializeObject(object value)
        {
            if (value == null) return null;
            var serializer = new DataContractJsonSerializer(value.GetType(), Settings);
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, value);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrEmpty(json)) return default(T);
            var serializer = new DataContractJsonSerializer(typeof(T), Settings);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(ms);
            }
        }

        public static object Deserialize(string json, Type type)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var serializer = new DataContractJsonSerializer(type, Settings);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return serializer.ReadObject(ms);
            }
        }
    }
}
