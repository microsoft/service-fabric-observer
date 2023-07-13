// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace FabricObserver.Observers.Utilities
{
    public static class JsonHelper
    {
        /// <summary>
        /// Determines if the supplied string is a serialized instance of the specified type T.
        /// </summary>
        /// <typeparam name="T">Type to be evaluated.</typeparam>
        /// <param name="text">Json string.</param>
        /// <param name="treatMissingMembersAsError">Optional boolean to treat missing type members as Error or not.</param>
        /// <returns>True if the string is a serialized instance of type T. False otherwise.</returns>
        public static bool IsJson<T>(string text, bool treatMissingMembersAsError = false)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                return TryDeserializeObject<T>(text, out _, treatMissingMembersAsError);
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
        /// <returns>A Json (string) representation of the supplied instance of type T or null upon failure.</returns>
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
        /// <param name="treatMissingMembersAsError">Optional boolean to treat missing type members as Error or not.</param>
        /// <returns>An instance of the specified type T or null if the string can't be deserialized into the specified type T.</returns>
        public static bool TryDeserializeObject<T>(string obj, out T data, bool treatMissingMembersAsError = false)
        {
            if (string.IsNullOrWhiteSpace(obj))
            {
                data = default;
                return false;
            }

            try
            {
                JsonSerializerSettings jsonSerializerSettings = new()
                {
                    MissingMemberHandling = treatMissingMembersAsError ? MissingMemberHandling.Error : MissingMemberHandling.Ignore
                };

                data = JsonConvert.DeserializeObject<T>(obj, jsonSerializerSettings);
                return true;
            }
            catch (JsonException)
            {

            }

            data = default;
            return false;
        }

        public static T ReadFromJsonStream<T>(Stream stream)
        {
            using StreamReader r = new(stream);
            string json = r.ReadToEnd();

            if (TryDeserializeObject(json, out T data))
            {
                return data;
            }

            return default;
        }

        public static T ConvertFromString<T>(string jsonInput)
        {
            Stream stream = CreateStreamFromString(jsonInput);
            return ReadFromJsonStream<T>(stream);  
        }

        private static Stream CreateStreamFromString(string input)
        {
            MemoryStream memoryStream = new();
            StreamWriter streamWriter = new(memoryStream, Encoding.UTF8);
            streamWriter.Write(input);
            streamWriter.Flush();
            memoryStream.Position = 0;
            return memoryStream;
        }
    }
}
