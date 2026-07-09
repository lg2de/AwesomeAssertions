using System.IO;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Build;

public static class CompressionExtensions
{
    /// <summary>
    /// Extracts a compressed tar archive (.tar.gz or .tar.xz). The compression method is detected
    /// automatically by SharpCompress' reader factory.
    /// </summary>
    public static void ExtractTar(string archive, string directory)
    {
        using Stream stream = File.OpenRead(archive);
        using IReader reader = ReaderFactory.OpenReader(stream);

        while (reader.MoveToNextEntry())
        {
            if (reader.Entry.IsDirectory)
            {
                continue;
            }

            reader.WriteEntryToDirectory(directory, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true,
            });
        }
    }
}
