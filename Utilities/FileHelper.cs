using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DeezFiles.Utilities
{
    public class FileHelper
    {
        public static async Task UploadFile(string filePath)
        {
            if (!string.IsNullOrEmpty(filePath))
            {
                await LocalFileHelper.SaveUPFileDetails(filePath);
                LocalFileHelper.CreateChunks(filePath);

            }
        }

        public static async Task DownloadFile(string filename)
        {
            string filehash = await LocalFileHelper.RetrieveHash(filename);
            if (string.IsNullOrEmpty(filehash))
            {
                Console.WriteLine($"[FileHelper] Could not find hash for file: {filename}");
                return;
            }

            // The .dn file contains the ordered list of shard hashes.
            string recreationInfoPath = Path.Combine(LocalFileHelper.uploadqueuePath, $"{filehash}.dn");
            if (!File.Exists(recreationInfoPath))
            {
                Console.WriteLine($"[FileHelper] Recreation info file not found for hash: {filehash}");
                return;
            }

            string[] shardHashes = (await File.ReadAllTextAsync(recreationInfoPath)).Split(';');

            // Hand off the download job to the orchestration service.
            FileDownloader.InitiateDownload(filename, shardHashes);
        }



    }
}