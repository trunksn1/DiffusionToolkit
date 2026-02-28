using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Diffusion.Common;
using Diffusion.IO;

namespace Diffusion.Toolkit.Services;

public class PerceptualHashService
{
    public async Task<int> BackfillHashes(Action<int, int> progressCallback, CancellationToken cancellationToken)
    {
        var dataStore = ServiceLocator.DataStore!;
        var totalMissing = dataStore.CountImagesWithoutPerceptualHash();

        if (totalMissing == 0)
            return 0;

        int processed = 0;
        int batchSize = 100;

        while (!cancellationToken.IsCancellationRequested)
        {
            var imageIds = dataStore.GetImagesWithoutPerceptualHash(batchSize);
            if (imageIds.Count == 0)
                break;

            var updates = new List<(int Id, long Hash)>();

            foreach (var id in imageIds)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var path = dataStore.GetImagePath(id);
                    if (path == null || !File.Exists(path))
                        continue;

                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var memStream = new MemoryStream();
                    await stream.CopyToAsync(memStream, cancellationToken);
                    memStream.Seek(0, SeekOrigin.Begin);

                    var hash = PerceptualHashHelper.ComputeDHash(memStream);
                    if (hash.HasValue)
                    {
                        updates.Add((id, hash.Value));
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"PerceptualHashService: Error hashing image {id}: {ex.Message}");
                }

                processed++;
                if (processed % 10 == 0)
                {
                    progressCallback?.Invoke(processed, totalMissing);
                }
            }

            if (updates.Count > 0)
            {
                dataStore.UpdatePerceptualHashes(updates);
            }

            progressCallback?.Invoke(processed, totalMissing);
        }

        return processed;
    }
}
