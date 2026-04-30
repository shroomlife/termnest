using System.Collections.ObjectModel;

namespace TermNest.Core.Sessions;

/// <summary>
/// In-memory hierarchical view of a flat <see cref="SessionData"/> list. The
/// tree is rebuilt by <see cref="BuildTree"/> from the SessionId path
/// ("Folder/Sub/SessionName").
/// </summary>
public sealed class SessionTreeNode
{
    public required string Name { get; init; }

    /// <summary>Folder path or session id depending on <see cref="IsFolder"/>.</summary>
    public required string Path { get; init; }

    public bool IsFolder { get; init; }

    public bool IsExpanded { get; set; }

    public SessionData? Session { get; init; }

    public ObservableCollection<SessionTreeNode> Children { get; } = new();

    /// <summary>
    /// Builds a folder hierarchy from a flat session collection. Sessions
    /// whose SessionId is "A/B/C" produce nested folders A &gt; B with the
    /// session as a leaf node "C". Folder names are sorted, sessions follow
    /// folders alphabetically.
    ///
    /// <paramref name="explicitFolders"/> ensures that folders without any
    /// sessions still appear in the tree — they wouldn't otherwise, since
    /// the structure is inferred from session paths.
    /// </summary>
    public static SessionTreeNode BuildTree(
        IReadOnlyList<SessionData> sessions,
        IReadOnlyCollection<string>? explicitFolders = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        SessionTreeNode root = new() { Name = "Sessions", Path = string.Empty, IsFolder = true };

        // Build folder skeleton + place sessions.
        foreach (SessionData session in sessions.OrderBy(s => s.SessionId, StringComparer.OrdinalIgnoreCase))
        {
            string id = session.SessionId;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            string[] segments = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
            SessionTreeNode parent = root;

            // All segments except the last are folder names.
            for (int i = 0; i < segments.Length - 1; i++)
            {
                string folderName = segments[i];
                string folderPath = string.Join('/', segments[..(i + 1)]);
                parent = EnsureFolder(parent, folderName, folderPath);
            }

            string leafName = segments[^1];
            parent.Children.Add(new SessionTreeNode
            {
                Name = leafName,
                Path = id,
                IsFolder = false,
                Session = session,
            });
        }

        // Stitch in folders that have no sessions yet so the user's "New
        // folder" / "Empty folder" entries persist across reloads.
        if (explicitFolders != null)
        {
            foreach (string folderPath in explicitFolders.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                string[] segments = folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                SessionTreeNode parent = root;
                for (int i = 0; i < segments.Length; i++)
                {
                    string folderName = segments[i];
                    string subPath = string.Join('/', segments[..(i + 1)]);
                    parent = EnsureFolder(parent, folderName, subPath);
                }
            }
        }

        SortRecursive(root);
        return root;
    }

    private static SessionTreeNode EnsureFolder(SessionTreeNode parent, string folderName, string folderPath)
    {
        SessionTreeNode? existing = parent.Children
            .FirstOrDefault(c => c.IsFolder && string.Equals(c.Name, folderName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            return existing;
        }

        SessionTreeNode created = new() { Name = folderName, Path = folderPath, IsFolder = true };
        parent.Children.Add(created);
        return created;
    }

    private static void SortRecursive(SessionTreeNode node)
    {
        SessionTreeNode[] sorted = node.Children
            .OrderByDescending(c => c.IsFolder)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        node.Children.Clear();
        foreach (SessionTreeNode child in sorted)
        {
            node.Children.Add(child);
            if (child.IsFolder)
            {
                SortRecursive(child);
            }
        }
    }
}
