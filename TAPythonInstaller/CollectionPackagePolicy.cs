using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TAPythonInstaller;

public sealed record ToolCollectionDescriptor(
    string Id,
    string Role,
    string UiKind,
    string ModuleId,
    IReadOnlyList<string> CategoryPath,
    string RequiredShellVersion,
    string SourceSha256);

public sealed record InstalledCollectionEntry(string CollectionId, string Role, string ModuleId, string Version);

public sealed record CollectionInstallCandidate(string Slug, string Version, ToolCollectionDescriptor Collection);

public static class ToolCollectionPolicy
{
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private static readonly Regex CollectionIdPattern = new("^[a-z0-9-]+$", RegexOptions.Compiled);
    private static readonly Regex ModuleIdPattern = new("^[a-z0-9_-]+$", RegexOptions.Compiled);
    private static readonly Regex Sha256Pattern = new("^[a-f0-9]{64}$", RegexOptions.Compiled);

    public static ToolCollectionDescriptor? Parse(JsonObject manifest)
    {
        if (manifest["collection"] is null) return null;
        if (manifest["collection"] is not JsonObject collection)
            throw new InvalidOperationException("manifest.json 的 collection 必须是对象。");

        var id = RequiredString(collection, "id");
        if (!CollectionIdPattern.IsMatch(id))
            throw new InvalidOperationException($"collection.id 无效：{id}");

        var role = RequiredString(collection, "role");
        if (role == "shell")
        {
            RejectUnknownFields(collection, "id", "role", "uiKind");
            var uiKind = OptionalString(collection, "uiKind");
            if (collection.ContainsKey("uiKind") && string.IsNullOrWhiteSpace(uiKind))
                throw new InvalidOperationException("collection.uiKind 不能为空。");
            return new ToolCollectionDescriptor(
                id,
                role,
                uiKind,
                string.Empty,
                [],
                string.Empty,
                string.Empty);
        }

        if (role != "module")
            throw new InvalidOperationException($"不支持的 collection.role：{role}");

        RejectUnknownFields(collection, "id", "role", "moduleId", "categoryPath", "requiredShellVersion", "sourceSha256");
        var moduleId = RequiredString(collection, "moduleId");
        if (!ModuleIdPattern.IsMatch(moduleId))
            throw new InvalidOperationException($"collection.moduleId 无效：{moduleId}");
        var categoryPath = RequiredStringArray(collection, "categoryPath");
        if (categoryPath.Count == 0)
            throw new InvalidOperationException("module collection.categoryPath 不能为空。");
        var requiredShellVersion = RequiredString(collection, "requiredShellVersion");
        var sourceSha256 = RequiredString(collection, "sourceSha256");
        if (!Sha256Pattern.IsMatch(sourceSha256))
            throw new InvalidOperationException("collection.sourceSha256 必须是 64 位小写 SHA256。");

        return new ToolCollectionDescriptor(
            id,
            role,
            string.Empty,
            moduleId,
            categoryPath,
            requiredShellVersion,
            sourceSha256);
    }

    public static void ValidateInstall(ToolCollectionDescriptor? collection, IEnumerable<InstalledCollectionEntry> installed)
    {
        if (collection is null || collection.Role == "shell") return;

        var shell = installed.FirstOrDefault(entry =>
            entry.CollectionId == collection.Id &&
            entry.Role == "shell");
        if (shell is null)
            throw new InvalidOperationException($"模块 {collection.ModuleId} requires Collection Shell {collection.Id}。请先安装工具集 Shell。");
        if (!string.Equals(NormalizeVersion(shell.Version), NormalizeVersion(collection.RequiredShellVersion), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"模块 {collection.ModuleId} requires Shell {collection.RequiredShellVersion}，当前已安装 {shell.Version}。");
    }

    public static bool CanUninstallShell(ToolCollectionDescriptor? collection, IEnumerable<InstalledCollectionEntry> installed)
    {
        if (collection is null || collection.Role != "shell") return true;
        return !installed.Any(entry => entry.CollectionId == collection.Id && entry.Role == "module");
    }

    public static bool HasSameIdentity(ToolCollectionDescriptor? left, ToolCollectionDescriptor? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Id != right.Id || left.Role != right.Role) return false;
        return left.Role != "module" || left.ModuleId == right.ModuleId;
    }

    public static IReadOnlyList<CollectionInstallCandidate> PlanModuleInstall(
        ToolCollectionDescriptor shell,
        string shellVersion,
        IEnumerable<CollectionInstallCandidate> candidates)
    {
        if (shell.Role != "shell")
            throw new InvalidOperationException("只有 Collection Shell 可以发起工具集安装。");

        var normalizedShellVersion = shellVersion.Trim().TrimStart('v', 'V');
        var modules = candidates
            .Where(candidate =>
                candidate.Collection.Role == "module" &&
                candidate.Collection.Id == shell.Id &&
                string.Equals(candidate.Collection.RequiredShellVersion, normalizedShellVersion, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => string.Join("/", candidate.Collection.CategoryPath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Collection.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var duplicateModule = modules
            .GroupBy(candidate => candidate.Collection.ModuleId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateModule is not null)
            throw new InvalidOperationException($"Collection {shell.Id} 包含重复模块：{duplicateModule.Key}");

        return modules;
    }

    public static string ResolvePythonRootPrefix(string pythonRoot)
    {
        var normalized = pythonRoot.Trim().Replace('\\', '/').Trim('/');
        if (normalized.StartsWith("Python/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["Python/".Length..];
        return normalized.Trim('/');
    }

    public static bool IsIgnorableCompatibilityFile(ToolCollectionDescriptor? collection, string path, long size, string sha256)
    {
        return collection?.Role == "module" &&
               string.Equals(path.Replace('\\', '/'), "Python/UEPythonToolkit/__init__.py", StringComparison.Ordinal) &&
               size == 0 &&
               string.Equals(sha256, EmptySha256, StringComparison.Ordinal);
    }

    private static string NormalizeVersion(string version) => version.Trim().TrimStart('v', 'V');

    private static void RejectUnknownFields(JsonObject collection, params string[] allowedFields)
    {
        var allowed = new HashSet<string>(allowedFields, StringComparer.Ordinal);
        foreach (var field in collection.Select(property => property.Key))
        {
            if (!allowed.Contains(field))
                throw new InvalidOperationException($"collection 包含未知字段 {field}。");
        }
    }

    private static string RequiredString(JsonObject value, string propertyName)
    {
        var text = OptionalString(value, propertyName);
        return string.IsNullOrWhiteSpace(text)
            ? throw new InvalidOperationException($"collection 缺少 {propertyName}。")
            : text;
    }

    private static string OptionalString(JsonObject value, string propertyName)
    {
        if (value[propertyName] is null) return string.Empty;
        if (value[propertyName] is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)) return text.Trim();
        throw new InvalidOperationException($"collection.{propertyName} 必须是字符串。");
    }

    private static List<string> RequiredStringArray(JsonObject value, string propertyName)
    {
        if (value[propertyName] is not JsonArray array)
            throw new InvalidOperationException($"collection.{propertyName} 必须是字符串数组。");
        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException($"collection.{propertyName} 包含无效项。");
            result.Add(text.Trim());
        }
        return result;
    }
}
