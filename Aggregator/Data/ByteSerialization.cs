using System.IO;
using System.Runtime.Serialization.Formatters.Binary;

namespace Aggregator
{
    /// <summary>
    /// This class implements the conversion from custom Objects to byte[] and vice versa/
    /// Every class that uses these static methods has to be [Serializable]
    /// </summary>
    public class ByteSerialization
    {
        public static byte[] ObjectToByteArray(object obj)
        {
            BinaryFormatter bf = new BinaryFormatter();
            using var ms = new MemoryStream();
            bf.Serialize(ms, obj);

            return ms.ToArray();
        }

        public static object ByteArrayToObject(byte[] arrBytes)
        {
            using var memStream = new MemoryStream();
            var binForm = new BinaryFormatter();
            memStream.Write(arrBytes, 0, arrBytes.Length);
            memStream.Seek(0, SeekOrigin.Begin);
            var obj = binForm.Deserialize(memStream);

            return obj;
        }
    }
}
