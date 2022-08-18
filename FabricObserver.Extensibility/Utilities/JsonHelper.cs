// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;
using System.IO;
using System.Net.Http.Formatting;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace FabricObserver.Observers.Utilities
{
    public static class JsonHelper
    {
        public static MediaTypeFormatter JsonMediaTypeFormatter =>
            new JsonMediaTypeFormatter
            {
                SerializerSettings = MediaTypeFormatterSettings,
                UseDataContractJsonSerializer = false
            };

        /// <summary>
        /// Determines if the supplied string is a serialized instance of the specified type T.
        /// </summary>
        /// <typeparam name="T">Type to be evaluated.</typeparam>
        /// <param name="text">Json string.</param>
        /// <returns>True if the string is a serialized instance of type T. False otherwise.</returns>
        public static bool IsJson<T>(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                return TryDerializeObject<T>(text, out _);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Tries to serialize an instance of the supplied type.
        /// </summary>
        /// <typeparam name="T">Input type.</typeparam>
        /// <param name="obj">Instance of type T.</param>
        /// <param name="data">out: the Json-serialized instance of the supplied type T.</param>
        /// <returns>A Json (string) representation of the supplied instance of type T.</returns>
        public static bool TrySerializeObject<T>(T obj, out string data)
        {
            if (obj == null)
            {
                data = null;
                return false;
            }

            try
            {
                data = JsonConvert.SerializeObject(obj);
                return true;
            }
            catch (JsonException)
            {

            }
            
            data = null;
            return false;
        }

        /// <summary>
        /// Tries to deserialize a Json string into an instance of specified type T.
        /// </summary>
        /// <typeparam name="T">Target type.</typeparam>
        /// <param name="obj">Json string representing an instance of type T.</param>
        /// <param name="data">out: an instance of type T.</param>
        /// <returns>An instance of the specified type T or null if the string can't be deserialized into the specified type T. Note: Missing members are treated as Error.</returns>
        public static bool TryDerializeObject<T>(string obj, out T data)
        {
            if (string.IsNullOrWhiteSpace(obj))
            {
                data = default;
                return false;
            }

            try
            {
                data = JsonConvert.DeserializeObject<T>(obj, new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });
                return true;
            }
            catch (JsonException)
            {

            }

            data = default;
            return false;
        }

        private static readonly JsonSerializerSettings MediaTypeFormatterSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Converters = new List<JsonConverter>
            {
                new StringEnumConverter { NamingStrategy = new CamelCaseNamingStrategy() }
            },
            TypeNameHandling = TypeNameHandling.Auto
        };

        public static T ReadFromJsonStream<T>(Stream stream)
        {
            var data = (T)JsonMediaTypeFormatter.ReadFromStreamAsync(
                typeof(T),
                stream,
                null,
                null).Result;

            return data;
        }

        public static T ConvertFromString<T>(string jsonInput)
        {
            using (var stream = CreateStreamFromString(jsonInput))
            {
                return ReadFromJsonStream<T>(stream);
            }
        }

        public static void WriteToStream<T>(T data, Stream stream)
        {
            JsonMediaTypeFormatter.WriteToStreamAsync(
                typeof(T),
                data,
                stream,
                null,
                null).Wait();
        }

        public static string ConvertToString<T>(T data)
        {
            using (var stream = new MemoryStream())
            {
                WriteToStream(data, stream);
                stream.Position = 0;
                return Encoding.UTF8.GetString(stream.GetBuffer());
            }
        }

        private static Stream CreateStreamFromString(string s)
        {
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(s);
            writer.Flush();
            stream.Position = 0;
            return stream;
        }
    }
}
