using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Diffusion.Common;
using Diffusion.Database;
using Diffusion.IO;
using Diffusion.Toolkit.Classes;
using Diffusion.Toolkit.Services;

namespace Diffusion.Toolkit
{
    public partial class DuplicateFinderWindow : BorderlessWindow
    {
        private readonly DuplicateFinderModel _model;
        private CancellationTokenSource? _cts;
        private int _targetImageId;
        private long _targetHash;

        public DuplicateFinderWindow()
        {
            _model = new DuplicateFinderModel();
            _model.Escape = new RelayCommand<object>(o => Close());

            InitializeComponent();
            DataContext = _model;
        }

        public void SetTargetImage(int imageId, long hash)
        {
            _targetImageId = imageId;
            _targetHash = hash;
            _model.Status = "Click Scan to find similar images";
            ScanButton.Content = "Find Similar";
        }

        private async void Scan_OnClick(object sender, RoutedEventArgs e)
        {
            if (_model.IsScanning)
                return;

            if (_targetHash != 0)
            {
                FindSimilarTo(_targetImageId, _targetHash);
                return;
            }

            _model.IsScanning = true;
            _model.DuplicateGroups.Clear();
            _model.Status = "Loading hashes...";
            ScanButton.IsEnabled = false;

            _cts = new CancellationTokenSource();

            try
            {
                var dataStore = ServiceLocator.DataStore!;
                var maxDistance = _model.MaxDistance;

                // Check if any hashes exist
                var allHashes = await Task.Run(() => dataStore.GetAllPerceptualHashes());
                if (allHashes.Count == 0)
                {
                    var missing = dataStore.CountImagesWithoutPerceptualHash();
                    _model.Status = $"No perceptual hashes found. {missing:N0} images need hashing.";
                    MessageBox.Show(
                        "No images have perceptual hashes computed yet.\n\nGo to Tools → Backfill Perceptual Hashes first to compute hashes for your existing images.\n\nNewly scanned images will get hashes automatically.",
                        "No Hashes Available", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var groups = await Task.Run(() => FindDuplicateGroups(dataStore, maxDistance, _cts.Token));

                _model.DuplicateGroups = new ObservableCollection<DuplicateGroup>(groups);
                _model.TotalGroups = groups.Count;
                _model.TotalDuplicates = groups.Sum(g => g.Count - 1);
                _model.Status = $"Scan complete. {groups.Count} duplicate group(s) found ({allHashes.Count:N0} images compared).";
            }
            catch (OperationCanceledException)
            {
                _model.Status = "Scan cancelled";
            }
            catch (Exception ex)
            {
                _model.Status = $"Error: {ex.Message}";
                Logger.Log($"DuplicateFinder: {ex.Message}");
            }
            finally
            {
                _model.IsScanning = false;
                ScanButton.IsEnabled = true;
            }
        }

        private List<DuplicateGroup> FindDuplicateGroups(DataStore dataStore, int maxDistance, CancellationToken token)
        {
            var allHashes = dataStore.GetAllPerceptualHashes();
            int count = allHashes.Count;

            // Extract into flat arrays for cache-friendly access in the hot loop
            var ids = new int[count];
            var hashes = new long[count];
            for (int i = 0; i < count; i++)
            {
                ids[i] = allHashes[i].Id;
                hashes[i] = allHashes[i].PerceptualHash;
            }

            Dispatcher.BeginInvoke(() =>
            {
                _model.ProgressMax = count;
                _model.Status = $"Comparing {count:N0} images across {Environment.ProcessorCount} threads...";
            });

            // Phase 1: Find all similar pairs using parallel processing.
            // The inner loop is pure arithmetic on flat arrays — no object access,
            // no HashSet lookups — so it benefits heavily from CPU cache and SIMD.
            int progressCounter = 0;
            var pairBag = new ConcurrentBag<(int A, int B)>();

            Parallel.For(0, count, new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            }, i =>
            {
                var hi = hashes[i];
                for (int j = i + 1; j < count; j++)
                {
                    if (BitOperations.PopCount(unchecked((ulong)(hi ^ hashes[j]))) <= maxDistance)
                    {
                        pairBag.Add((i, j));
                    }
                }

                var p = Interlocked.Increment(ref progressCounter);
                if (p % 500 == 0)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        _model.Progress = p;
                        _model.Status = $"Comparing image {p:N0} of {count:N0}...";
                    });
                }
            });

            token.ThrowIfCancellationRequested();

            // Phase 2: Greedy representative clustering on discovered pairs.
            // Build adjacency list, then greedily assign each unvisited node
            // as a group representative — its unassigned neighbors join directly.
            Dispatcher.BeginInvoke(() =>
            {
                _model.Status = $"Clustering {pairBag.Count:N0} similar pairs...";
            });

            var neighbors = new Dictionary<int, List<int>>();
            foreach (var (a, b) in pairBag)
            {
                if (!neighbors.TryGetValue(a, out var listA))
                {
                    listA = new List<int>();
                    neighbors[a] = listA;
                }
                listA.Add(b);

                if (!neighbors.TryGetValue(b, out var listB))
                {
                    listB = new List<int>();
                    neighbors[b] = listB;
                }
                listB.Add(a);
            }

            var assigned = new HashSet<int>();
            var clusters = new List<(int Rep, List<int> Members)>();

            foreach (var rep in neighbors.Keys.OrderBy(k => k))
            {
                if (assigned.Contains(rep))
                    continue;

                assigned.Add(rep);
                var members = new List<int> { rep };

                foreach (var nb in neighbors[rep])
                {
                    if (!assigned.Contains(nb))
                    {
                        members.Add(nb);
                        assigned.Add(nb);
                    }
                }

                if (members.Count > 1)
                    clusters.Add((rep, members));
            }

            // Phase 3: Load image details from DB and build result models
            var allMemberIds = clusters
                .SelectMany(c => c.Members)
                .Select(idx => ids[idx])
                .Distinct()
                .ToList();

            Dispatcher.BeginInvoke(() =>
            {
                _model.Status = $"Loading details for {allMemberIds.Count:N0} images in {clusters.Count} groups...";
            });

            var allDetails = dataStore.GetImageDetails(allMemberIds, (current, total) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _model.Status = $"Loading image details... batch {current} of {total}";
                });
            });
            var detailsLookup = allDetails.ToDictionary(d => d.Id);

            Dispatcher.BeginInvoke(() =>
            {
                _model.Status = $"Building {clusters.Count} duplicate groups...";
            });

            var result = new List<DuplicateGroup>();
            int groupIndex = 1;

            foreach (var (rep, members) in clusters)
            {
                token.ThrowIfCancellationRequested();

                var dupGroup = new DuplicateGroup { GroupIndex = groupIndex++ };
                var referenceHash = hashes[rep];

                foreach (var idx in members)
                {
                    if (detailsLookup.TryGetValue(ids[idx], out var details))
                    {
                        var distance = BitOperations.PopCount(unchecked((ulong)(referenceHash ^ details.PerceptualHash)));
                        dupGroup.Items.Add(new DuplicateGroupItem
                        {
                            Id = details.Id,
                            Path = details.Path,
                            FileName = System.IO.Path.GetFileName(details.Path),
                            Width = details.Width,
                            Height = details.Height,
                            FileSize = details.FileSize,
                            Distance = distance,
                            Similarity = PerceptualHashHelper.DistanceToSimilarity(distance),
                            CreatedDate = details.CreatedDate,
                            Rating = details.Rating,
                            Favorite = details.Favorite
                        });
                    }
                }

                if (dupGroup.Items.Count > 1)
                    result.Add(dupGroup);
            }

            return result;
        }

        public async void FindSimilarTo(int imageId, long hash)
        {
            _model.IsScanning = true;
            _model.Status = "Finding similar images...";
            ScanButton.IsEnabled = false;

            try
            {
                var dataStore = ServiceLocator.DataStore!;

                // Use a generous threshold for "find similar" (50% = distance 32)
                var maxDistance = Math.Max(_model.MaxDistance, 32);

                var results = await Task.Run(() => dataStore.FindSimilarImages(hash, maxDistance, imageId));

                var group = new DuplicateGroup { GroupIndex = 1 };

                // Add the reference image first
                var refImage = dataStore.GetImage(imageId);
                if (refImage != null)
                {
                    group.Items.Add(new DuplicateGroupItem
                    {
                        Id = refImage.Id,
                        Path = refImage.Path,
                        FileName = System.IO.Path.GetFileName(refImage.Path),
                        Width = refImage.Width,
                        Height = refImage.Height,
                        FileSize = refImage.FileSize,
                        Distance = 0,
                        Similarity = 100.0,
                        CreatedDate = refImage.CreatedDate,
                        Rating = refImage.Rating,
                        Favorite = refImage.Favorite
                    });
                }

                foreach (var r in results.OrderBy(r => HammingDistance(hash, r.PerceptualHash)))
                {
                    var distance = HammingDistance(hash, r.PerceptualHash);
                    group.Items.Add(new DuplicateGroupItem
                    {
                        Id = r.Id,
                        Path = r.Path,
                        FileName = System.IO.Path.GetFileName(r.Path),
                        Width = r.Width,
                        Height = r.Height,
                        FileSize = r.FileSize,
                        Distance = distance,
                        Similarity = PerceptualHashHelper.DistanceToSimilarity(distance),
                        CreatedDate = r.CreatedDate,
                        Rating = r.Rating,
                        Favorite = r.Favorite
                    });
                }

                if (group.Items.Count > 1)
                {
                    _model.DuplicateGroups = new ObservableCollection<DuplicateGroup> { group };
                    _model.TotalGroups = 1;
                    _model.TotalDuplicates = group.Items.Count - 1;
                }
                else
                {
                    _model.TotalGroups = 0;
                    _model.TotalDuplicates = 0;
                }

                _model.Status = $"Found {results.Count} similar image(s)";
            }
            catch (Exception ex)
            {
                _model.Status = $"Error: {ex.Message}";
                Logger.Log($"FindSimilarTo: {ex.Message}");
            }
            finally
            {
                _model.IsScanning = false;
                ScanButton.IsEnabled = true;
            }
        }

        private void MarkForDeletion_OnClick(object sender, RoutedEventArgs e)
        {
            var selectedItems = _model.DuplicateGroups
                .SelectMany(g => g.Items)
                .Where(i => i.IsSelected)
                .ToList();

            if (!selectedItems.Any())
            {
                MessageBox.Show("No images selected. Check the boxes on images you want to mark for deletion.",
                    "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                $"Mark {selectedItems.Count} image(s) for deletion?",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var ids = selectedItems.Select(i => i.Id).ToList();
                ServiceLocator.DataStore!.SetDeleted(ids, true);
                _model.Status = $"Marked {ids.Count} images for deletion";
            }
        }

        private static int HammingDistance(long hash1, long hash2)
        {
            return BitOperations.PopCount(unchecked((ulong)(hash1 ^ hash2)));
        }
    }
}
