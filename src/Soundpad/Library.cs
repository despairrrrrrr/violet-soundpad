using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace VioletSoundpad;

[DataContract]
public sealed class SoundFolder
{
    [DataMember] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [DataMember] public string Name { get; set; } = "";
}

[DataContract]
public sealed class SoundClip
{
    [DataMember] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [DataMember] public string Name { get; set; } = "";
    [DataMember] public string Extension { get; set; } = "";
    [DataMember] public double Duration { get; set; }
    [DataMember] public string? FolderId { get; set; }

    public string Format => Extension.TrimStart('.').ToUpperInvariant();
    public string Length => FormatTime(TimeSpan.FromSeconds(Duration));

    internal static string FormatTime(TimeSpan time) =>
        ((int)time.TotalMinutes).ToString(CultureInfo.InvariantCulture) + ":" +
        time.Seconds.ToString("D2", CultureInfo.InvariantCulture);
}

[DataContract]
public sealed class Library
{
    [DataMember] public List<SoundFolder> Folders { get; set; } = new();
    [DataMember] public List<SoundClip> Clips { get; set; } = new();
    [DataMember] public string? MicrophoneId { get; set; }
    [DataMember] public string? CableId { get; set; }
    [DataMember] public string? MonitorId { get; set; }
    [DataMember] public bool MonitorEnabled { get; set; }
    [DataMember] public double Volume { get; set; } = 0.8;

    private static readonly DataContractJsonSerializer Serializer = new(typeof(Library));

    private static readonly string DirectoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VioletSoundpad");
    private static string LibraryPath => Path.Combine(DirectoryPath, "library.json");
    private static string ClipDirectory => Path.Combine(DirectoryPath, "clips");

    public static Library Load()
    {
        if (!File.Exists(LibraryPath))
            return new Library();

        using var file = File.OpenRead(LibraryPath);
        var state = (Library?)Serializer.ReadObject(file) ?? throw new SerializationException("Пустая библиотека.");
        state.Folders ??= new List<SoundFolder>();
        state.Clips ??= new List<SoundClip>();
        return state;
    }

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        string pending = LibraryPath + ".tmp";
        using (var file = File.Create(pending))
        {
            Serializer.WriteObject(file, this);
            file.Flush(true);
        }

        if (File.Exists(LibraryPath))
            File.Replace(pending, LibraryPath, LibraryPath + ".bak");
        else
            File.Move(pending, LibraryPath);
    }

    public static string GetPath(SoundClip clip)
    {
        if (!Guid.TryParseExact(clip.Id, "N", out _) || !IsSupported(clip.Extension))
            throw new InvalidDataException("Некорректное имя файла в библиотеке.");
        return Path.Combine(ClipDirectory, clip.Id + clip.Extension);
    }

    public static bool IsSupported(string extension) =>
        extension is ".wav" or ".mp3" or ".flac" or ".m4a" or ".aac" or ".wma" or ".aiff" or ".aif";

    public static SoundClip Import(string source, string? folderId)
    {
        string extension = Path.GetExtension(source).ToLowerInvariant();
        if (!IsSupported(extension))
            throw new InvalidDataException("Формат не поддерживается.");

        var clip = new SoundClip
        {
            Name = Path.GetFileNameWithoutExtension(source),
            Extension = extension,
            FolderId = folderId
        };
        Directory.CreateDirectory(ClipDirectory);
        string target = GetPath(clip);
        try
        {
            File.Copy(source, target);
            using var reader = AudioEngine.OpenFile(target);
            clip.Duration = reader.TotalTime.TotalSeconds;
            if (clip.Duration <= 0 || double.IsNaN(clip.Duration) || double.IsInfinity(clip.Duration))
                throw new InvalidDataException("В файле нет звуковой дорожки.");
            return clip;
        }
        catch
        {
            TryDeleteFile(clip);
            throw;
        }
    }

    public static void TryDeleteFile(SoundClip clip)
    {
        try { File.Delete(GetPath(clip)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
