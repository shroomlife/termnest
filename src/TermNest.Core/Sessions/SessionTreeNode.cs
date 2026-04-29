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
    /// folders alphabetically — matches the v3 default sort.
    /// </summary>
    public static SessionTreeNode BuildTree(IReadOnlyList<SessionData> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        SessionTreeNode root = new() { Name = "PuTTY Sessions", Path = string.Empty, IsFolder = true };

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
                SessionTreeNode? existing = parent.Children
                    .FirstOrDefault(c => c.IsFolder && string.Equals(c.Name, folderName, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new SessionTreeNode { Name = folderName, Path = folderPath, IsFolder = true };
                    parent.Children.Add(existing);
                }
                parent = existing;
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

        SortRecursive(root);
        return root;
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
