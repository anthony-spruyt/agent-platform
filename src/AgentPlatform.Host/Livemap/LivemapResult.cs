using System.Text.Json;
using AgentPlatform.Host.Agents;

namespace AgentPlatform.Host.Livemap;

public enum LivemapResultStatus
{
    Data,
    Code,
    Partial,
    GaveUp,
}

public sealed record LivemapFile(string Path, string Content);

public sealed record LivemapResult(LivemapResultStatus Status, string Summary, string? BuildsJson, IReadOnlyList<LivemapFile> Files, string? Notes)
{
    public const int MaxSummaryLength = 4000;
    public const int MaxNotesLength = 8000;
    public const int MaxBuildsJsonLength = 48 * 1024;
    public const int MaxFiles = 10;
    public const int MaxFileLength = 32 * 1024;
    public const int MaxFilesLength = 40 * 1024;

    private const string SelfTestPath = "tools/selftest.cpp";

    private static readonly string[] _directories = ["src/", "tests/", "data/"];

    public static bool TryParse(SubmitResultPayload payload, out LivemapResult? result, out string? error)
    {
        ArgumentNullException.ThrowIfNull(payload);
        result = null;
        error = Check(payload, out var parsed);
        if (error is not null)
        {
            return false;
        }

        result = parsed;
        return true;
    }

    // The same limits apply where the worker commits the files, so a stored result is held to them again.
    public static string? CheckFiles(IReadOnlyList<LivemapFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count > MaxFiles)
        {
            return $"data.files may hold at most {MaxFiles} files.";
        }

        foreach (var file in files)
        {
            if (PathError(file.Path) is { } pathError)
            {
                return pathError;
            }

            if (file.Content.Length > MaxFileLength)
            {
                return $"The content of {file.Path} is over {MaxFileLength} characters.";
            }
        }

        return files.Sum(f => f.Content.Length) > MaxFilesLength
            ? $"The files together are over {MaxFilesLength} characters."
            : ClashError(files);
    }

    private static string? Check(SubmitResultPayload payload, out LivemapResult? parsed)
    {
        parsed = null;
        if (payload.Status != "success")
        {
            return "status is not \"success\"; report the outcome in data.status and submit with status \"success\".";
        }

        if (string.IsNullOrWhiteSpace(payload.Summary) || payload.Summary.Length > MaxSummaryLength)
        {
            return $"summary is required and must be at most {MaxSummaryLength} characters.";
        }

        if (payload.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            return "data must be a JSON object with status and, by status, builds_json, files and notes.";
        }

        if (ReadStatus(data) is not { } status)
        {
            return "data.status is missing or is not one of data, code, partial or gave_up.";
        }

        if (!ReadOptionalText(data, "notes", MaxNotesLength, out var notes))
        {
            return $"data.notes must be a string of at most {MaxNotesLength} characters.";
        }

        if (status == LivemapResultStatus.Partial && string.IsNullOrWhiteSpace(notes))
        {
            return "A partial result needs data.notes saying what you found, so the next run can carry on.";
        }

        string? buildsJson = null;
        IReadOnlyList<LivemapFile> files = [];
        if (status is LivemapResultStatus.Data or LivemapResultStatus.Code
            && ReadPatch(data, status, out buildsJson, out files) is { } error)
        {
            return error;
        }

        parsed = new LivemapResult(status, payload.Summary, buildsJson, files, notes);
        return null;
    }

    private static LivemapResultStatus? ReadStatus(JsonElement data) =>
        data.TryGetProperty("status", out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() switch
            {
                "data" => LivemapResultStatus.Data,
                "code" => LivemapResultStatus.Code,
                "partial" => LivemapResultStatus.Partial,
                "gave_up" => LivemapResultStatus.GaveUp,
                _ => null,
            }
            : null;

    private static bool ReadOptionalText(JsonElement data, string name, int max, out string? text)
    {
        text = null;
        if (!data.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        text = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return text is not null && text.Length <= max;
    }

    private static string? ReadPatch(JsonElement data, LivemapResultStatus status, out string? buildsJson, out IReadOnlyList<LivemapFile> files)
    {
        files = [];
        buildsJson = data.TryGetProperty("builds_json", out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        if (string.IsNullOrWhiteSpace(buildsJson) || buildsJson.Length > MaxBuildsJsonLength)
        {
            buildsJson = null;
            return $"data.builds_json must be the full text of {LivemapMergeGate.BuildsPath} as a string of at most {MaxBuildsJsonLength} characters.";
        }

        var present = data.TryGetProperty("files", out var filesElement) && filesElement.ValueKind != JsonValueKind.Null;
        if (status == LivemapResultStatus.Data)
        {
            return present && !IsEmptyArray(filesElement)
                ? "data.files is for a code result only; a data result carries builds_json alone."
                : null;
        }

        if (!present || IsEmptyArray(filesElement))
        {
            return "A code result needs data.files with at least one file.";
        }

        return ReadFiles(filesElement, out files);
    }

    private static bool IsEmptyArray(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == 0;

    private static string? ReadFiles(JsonElement element, out IReadOnlyList<LivemapFile> files)
    {
        files = [];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxFiles)
        {
            return $"data.files must be an array of at most {MaxFiles} {{path, content}} objects.";
        }

        var read = new List<LivemapFile>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || Text(item, "path") is not { } path
                || Text(item, "content") is not { } content)
            {
                return "each data.files entry needs a string path and a string content.";
            }

            read.Add(new LivemapFile(path, content));
        }

        if (CheckFiles(read) is { } error)
        {
            return error;
        }

        files = read;
        return null;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? PathError(string path)
    {
        if (!IsPlainPath(path) || !(_directories.Any(d => path.StartsWith(d, StringComparison.Ordinal)) || path == SelfTestPath))
        {
            var shown = path.Length > 200 ? path[..200] : path;
            return $"The data.files path \"{shown}\" is not allowed: use plain segments under src/, tests/, data/ or exactly {SelfTestPath}.";
        }

        return string.Equals(path, LivemapMergeGate.BuildsPath, StringComparison.Ordinal)
            ? $"{LivemapMergeGate.BuildsPath} is carried by data.builds_json; leave it out of data.files."
            : null;
    }

    private static string? ClashError(IReadOnlyList<LivemapFile> files)
    {
        var paths = files.Select(f => f.Path).ToList();
        foreach (var path in paths)
        {
            if (paths.Count(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 1)
            {
                return $"The data.files path {path} is given more than once.";
            }

            if (paths.Any(p => p.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
            {
                return $"The data.files path {path} is both a file and a directory.";
            }
        }

        return null;
    }

    private static bool IsPlainPath(string path) =>
        path.Length > 0 && path.Split('/').All(segment => segment.Length > 0 && segment is not ("." or "..") && segment.All(IsPathChar));

    private static bool IsPathChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '@' or '+' or '~';
}
