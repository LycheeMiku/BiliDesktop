using System.IO;

namespace BiliDesktop;

internal sealed record BackupRemovalResult(int RemovedRules, int DeletedFiles);

internal static class BackupRuleRemoval
{
    internal static async Task<BackupRemovalResult> RemoveAsync(BackupStore store, DownloadQueue queue, IReadOnlyList<BackupRule> rules, bool deleteFiles)
    {
        var selected = rules.DistinctBy(r => r.Id).ToArray();
        foreach (var rule in selected) store.SetEnabled(rule.Id, false);
        var ids = selected.Select(r => r.Id).ToHashSet();
        var entries = queue.Items.Where(e => e.BackupRuleId is long id && ids.Contains(id)).ToArray();
        foreach (var entry in entries) entry.Retire();
        await Task.WhenAll(entries.Select(e => e.Completion));

        var deleted = 0;
        if (deleteFiles)
        {
            var paths = selected.SelectMany(rule => store.CompletedPaths(rule.Id).Select(path => (rule, path, fullPath: CheckedPath(rule.Directory, path)))).ToArray();
            var deletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rule, path, fullPath) in paths)
            {
                if (File.Exists(fullPath) && deletedPaths.Add(fullPath)) { File.Delete(fullPath); deleted++; }
                store.ForgetFavoriteFile(rule.AccountId, rule.FolderId, path);
            }
        }
        foreach (var rule in selected) store.Remove(rule.Id);
        return new(selected.Length, deleted);
    }

    private static string CheckedPath(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("记录的文件不在备份目录中：" + fullPath);
        var directory = Path.GetDirectoryName(fullPath);
        while (directory is not null)
        {
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("备份目录包含链接，未删除文件：" + fullPath);
            if (string.Equals(Path.TrimEndingDirectorySeparator(directory), fullRoot, StringComparison.OrdinalIgnoreCase)) break;
            directory = Path.GetDirectoryName(directory);
        }
        return fullPath;
    }
}
