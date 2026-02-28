using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Diffusion.IO;

public static class PerceptualHashHelper
{
    /// <summary>
    /// Computes a 64-bit difference hash (dHash) for the given image stream.
    /// The image is resized to 9x8 grayscale and adjacent pixels are compared.
    /// </summary>
    public static long? ComputeDHash(Stream stream)
    {
        try
        {
            stream.Seek(0, SeekOrigin.Begin);

            using var image = Image.Load<L8>(stream);
            image.Mutate(x => x.Resize(9, 8));

            long hash = 0;
            int bit = 0;

            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    var left = image[x, y].PackedValue;
                    var right = image[x + 1, y].PackedValue;

                    if (left > right)
                    {
                        hash |= (1L << bit);
                    }

                    bit++;
                }
            }

            return hash;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Computes the Hamming distance between two hashes.
    /// </summary>
    public static int HammingDistance(long hash1, long hash2)
    {
        long xor = hash1 ^ hash2;
        return BitCount(xor);
    }

    private static int BitCount(long value)
    {
        // Brian Kernighan's algorithm
        int count = 0;
        while (value != 0)
        {
            value &= value - 1;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Converts a Hamming distance (0-64) to a similarity percentage (100%-0%).
    /// </summary>
    public static double DistanceToSimilarity(int distance)
    {
        return (1.0 - (distance / 64.0)) * 100.0;
    }
}
