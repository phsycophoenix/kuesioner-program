using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KuesionerSkripsi.Core.Services;

/// <summary>
/// Service untuk resolusi video path dengan multiple fallback locations
/// Mendukung berbagai lokasi penyimpanan file video
/// </summary>
public static class VideoPathService
{
    public const string BundledVideoRelativePath = "Video\\Video.mp4";
    public const string BundledVideoFileName = "Video.mp4";

    /// <summary>
    /// Mencari dan mendapatkan path ke file video yang tersedia
    /// </summary>
    public static bool TryResolveBundledVideo(out string videoPath)
    {
        foreach (var candidate in GetCandidatePaths())
        {
            if (File.Exists(candidate))
            {
                var fileInfo = new FileInfo(candidate);
                if (fileInfo.Length > 0)
                {
                    videoPath = candidate;
                    LoggerService.LogInfo($"Video found at: {videoPath} ({fileInfo.Length / (1024 * 1024)}MB)");
                    return true;
                }
                else
                {
                    LoggerService.LogWarning($"Video file empty: {candidate}");
                }
            }
        }

        videoPath = string.Empty;
        LoggerService.LogWarning($"Video not found in any candidate path: {string.Join(", ", GetCandidatePaths())}");
        return false;
    }

    /// <summary>
    /// Mendapatkan daftar lokasi kandidat untuk file video
    /// Diurutkan berdasarkan prioritas
    /// </summary>
    public static IReadOnlyList<string> GetCandidatePaths()
    {
        var candidates = new List<string>();

        // 1. AppData Kuesioner folder (primary location - recommended)
        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kuesioner");
        candidates.Add(Path.Combine(appDataRoot, "Video Animasi Edukasi", BundledVideoFileName));

        // 2. Program folder
        var applicationFolder = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(applicationFolder, "Video", BundledVideoFileName));
        candidates.Add(Path.Combine(applicationFolder, BundledVideoFileName));

        // 3. Backward compatibility: D:\Kuesioner (legacy instalasi lama)
        candidates.Add(Path.Combine(@"D:\Kuesioner", "Video Animasi Edukasi", BundledVideoFileName));
        candidates.Add(Path.Combine(@"D:\Kuesioner", "Video", BundledVideoFileName));

        // 4. User Documents
        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        candidates.Add(Path.Combine(documentsPath, "Kuesioner", "Video", BundledVideoFileName));
        candidates.Add(Path.Combine(documentsPath, BundledVideoFileName));

        // 5. Desktop
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        candidates.Add(Path.Combine(desktopPath, BundledVideoFileName));

        // 6. Program Files
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(programFiles, "Kuesioner", "Video", BundledVideoFileName));

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Mendapatkan daftar folder yang akan di-search untuk video file
    /// Berguna untuk user guidance
    /// </summary>
    public static IReadOnlyList<string> GetSearchFolders()
    {
        var folders = new List<string>
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Kuesioner\\Video Animasi Edukasi"),
            Path.Combine(AppContext.BaseDirectory, "Video"),
            AppContext.BaseDirectory,
            @"D:\Kuesioner\Video Animasi Edukasi",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Kuesioner\\Video"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kuesioner\\Video")
        };

        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Validasi apakah file video valid (ada dan tidak kosong)
    /// </summary>
    public static bool IsValidVideoFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        try
        {
            var fileInfo = new FileInfo(filePath);
            return fileInfo.Exists && fileInfo.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
