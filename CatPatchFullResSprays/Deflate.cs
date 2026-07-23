using System;
using System.IO;
using System.IO.Compression;

namespace CatPatchFullResSprays;

public static class Deflate
{
    public static byte[] CompressData(byte[] data)
    {
        if (data == null)
            return null;
        if (data.Length == 0)
            return [];
        try
        {
            using MemoryStream sourceStream = new();
            using (DeflateStream deflateStream = new(sourceStream, CompressionLevel.Optimal, true))
                deflateStream.Write(data, 0, data.Length);
            return sourceStream.ToArray();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogInfo("Error while compressing: " + ex.Message);
            return null;
        }
    }

    public static byte[] DecompressData(byte[] data)
    {
        if (data == null)
            return null;
        if (data.Length == 0)
            return [];
        try
        {
            using MemoryStream destinationStream = new();
            
            using (MemoryStream sourceStream = new(data))
                using (DeflateStream deflateStream = new(sourceStream, CompressionMode.Decompress))
                    deflateStream.CopyTo(destinationStream);
            
            return destinationStream.ToArray();
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogInfo("Error while decompressing: " + ex.Message);
            return null;
        }
    }
}