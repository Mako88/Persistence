namespace Persistence.Services;

/// <summary>
/// Vector helpers for semantic recall: cosine similarity and the compact on-disk encoding for a
/// stored embedding. Kept free of storage and HTTP concerns so both sides can be tested directly.
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Cosine similarity of two equal-length vectors, in [-1, 1]. Returns 0 for mismatched lengths or
    /// a zero-magnitude vector rather than throwing or producing NaN: a vector we cannot compare is
    /// "unrelated", which lets one bad row drop out of the ranking instead of failing the recall.
    /// </summary>
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0, magA = 0, magB = 0;

        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            magA += (double)a[i] * a[i];
            magB += (double)b[i] * b[i];
        }

        if (magA <= 0 || magB <= 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }

    /// <summary>
    /// Packs a vector as little-endian float32 for BLOB storage. Little-endian explicitly (rather than
    /// whatever the host is) so a store stays readable if it is ever moved between machines — peer
    /// stores travel between containers and hosts.
    /// </summary>
    public static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];

        for (var i = 0; i < vector.Length; i++)
        {
            var slice = bytes.AsSpan(i * sizeof(float), sizeof(float));
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(slice, vector[i]);
        }

        return bytes;
    }

    /// <summary>
    /// Unpacks a little-endian float32 BLOB written by <see cref="ToBlob"/>. A truncated or otherwise
    /// unreadable blob yields an empty vector, which scores 0 and simply drops out of the ranking.
    /// </summary>
    public static float[] FromBlob(byte[]? blob)
    {
        if (blob == null || blob.Length == 0 || blob.Length % sizeof(float) != 0)
        {
            return [];
        }

        var vector = new float[blob.Length / sizeof(float)];

        for (var i = 0; i < vector.Length; i++)
        {
            var slice = blob.AsSpan(i * sizeof(float), sizeof(float));
            vector[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(slice);
        }

        return vector;
    }

    /// <summary>
    /// Stable content fingerprint used to detect that a fragment's text has changed since it was
    /// embedded. SHA-256 of the UTF-8 text, hex-encoded — content-addressed rather than
    /// timestamp-based so an edit that restores earlier text correctly reuses the existing vector.
    /// </summary>
    public static string ContentHash(string? text) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(text ?? "")));
}
