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
        public static MediaTypeFormatter JsonMediaTypeFormatter
        {
            get
            {
                return new JsonMediaTypeFormatter
                {
                    SerializerSettings = MediaTypeFormatterSettings,
                    UseDataContractJsonSerializer = false,
                };
            }
        }

        public static bool IsJson<T>(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                _ = JsonConvert.DeserializeObject<T>(text);
                return true;
            }
            catch (JsonSerializationException)
            {
                return false;
            }
            catch (JsonReaderException)
            {
                return false;
            }
            catch (JsonWriterException)
            {
                return false;
            }
        }

        private static readonly JsonSerializerSettings MediaTypeFormatterSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Converters = new List<JsonConverter>
            {
                new StringEnumConverter { NamingStrategy = new CamelCaseNamingStrategy() },
            },
            TypeNameHandling = TypeNameHandling.Auto,
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

        public static void WriteToStream<T>(T data, Stream stream)
        {
            JsonMediaTypeFormatter.WriteToStreamAsync(
                typeof(T),
                data,
                stream,
                null,
                null).Wait();
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

        public static T ConvertFromString<T>(string jsonInput)
        {
            using (var stream = CreateStreamFromString(jsonInput))
            {
                return ReadFromJsonStream<T>(stream);
            }
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
    }
}
